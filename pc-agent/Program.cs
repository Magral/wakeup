using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

internal static partial class Program
{
    private const int Port = 3000;
    private static readonly ConcurrentDictionary<string, Challenge> Challenges = new();
    private static readonly object LogLock = new();
    private static readonly CommandLedger Ledger = new();
    private static readonly SemaphoreSlim CommandGate = new(1, 1);
    private static DateTime? LastRequest;

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--install-firewall") return InstallFirewallRule();

        var baseDirectory = AppContext.BaseDirectory;
        byte[] key;
        try { key = LoadOrCreateKey(baseDirectory); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        using var stop = new CancellationTokenSource();
        var serverTask = RunServerAsync(baseDirectory, key, stop.Token);
        using var tray = new AgentTrayContext(baseDirectory);
        Application.Run(tray);
        stop.Cancel();
        try { serverTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        return 0;
    }

    private static byte[] LoadOrCreateKey(string baseDirectory)
    {
        var keyPath = Path.Combine(baseDirectory, "wake.key");
        if (!File.Exists(keyPath))
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var text = Convert.ToHexString(key);
            File.WriteAllText(keyPath, text);
            try { File.SetAttributes(keyPath, File.GetAttributes(keyPath) | FileAttributes.Hidden); } catch { }
            MessageBox.Show(
                "Создан ключ помощника. Введите его в Wake Mini на телефоне:\n\n" + text +
                "\n\nНе отправляйте и не публикуйте этот ключ.",
                "Wake Mini — новый ключ", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return key;
        }

        byte[] existing;
        try { existing = Convert.FromHexString(File.ReadAllText(keyPath).Trim()); }
        catch { throw new InvalidOperationException("Файл wake.key повреждён. Удалите его и запустите помощник снова, чтобы создать новый ключ."); }
        if (existing.Length != 32)
            throw new InvalidOperationException("В wake.key должен быть ключ из 64 шестнадцатеричных символов.");
        return existing;
    }

    private static async Task RunServerAsync(string baseDirectory, byte[] key, CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            Log(baseDirectory, $"UDP-порт {Port} открыт; помощник работает в трее.");
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await udp.ReceiveAsync(cancellationToken);
                _ = HandleDatagramAsync(udp, datagram, baseDirectory, key);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            Log(baseDirectory, $"Не удалось открыть UDP-порт {Port}: {ex.Message}");
            MessageBox.Show($"Не удалось открыть UDP-порт {Port}. Проверьте, не запущена ли другая копия Wake Mini.",
                "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task HandleDatagramAsync(UdpClient udp, UdpReceiveResult datagram, string baseDirectory, byte[] key)
    {
        try
        {
            var text = Encoding.UTF8.GetString(datagram.Buffer);
            var fields = text.Split('|');
            if (fields.Length == 3 && fields[0] == "WM1" && fields[1] == "PING" && IsNonce(fields[2]))
            {
                PruneChallenges();
                var expiry = DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds();
                if (!Challenges.TryAdd(fields[2], new Challenge(expiry, datagram.RemoteEndPoint))) return;
                var response = $"WM1|ONLINE|{fields[2]}|{Sign(key, $"ONLINE|{fields[2]}")}";
                await SendAsync(udp, response, datagram.RemoteEndPoint);
                return;
            }

            if (fields.Length != 4 || fields[0] != "WM1" || !IsAllowedCommand(fields[1]) || !IsNonce(fields[2])) return;
            var command = fields[1];
            var nonce = fields[2];
            if (!Verify(key, $"{command}|{nonce}", fields[3])) return;
            await CommandGate.WaitAsync();
            try
            {
                var endpoint = datagram.RemoteEndPoint.ToString();
                if (Ledger.Find(nonce, command, endpoint) is string previous)
                {
                    await SendAsync(udp, previous, datagram.RemoteEndPoint);
                    return;
                }
                if (!Challenges.TryRemove(nonce, out var challenge)
                    || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > challenge.ExpiresAt
                    || !challenge.RemoteEndPoint.Equals(datagram.RemoteEndPoint)) return;
                LastRequest = DateTime.Now;
                string verb;
                if (command == "LAUNCH" || command.StartsWith("PROFILE_"))
                {
                    var profile = command == "LAUNCH" ? null : command[8..].ToLowerInvariant();
                    verb = LaunchApplication(baseDirectory, out var error, profile)
                        ? command + "_ACCEPTED"
                        : error == "not_configured" ? command + "_NOT_CONFIGURED" : command + "_FAILED";
                    Log(baseDirectory, $"Команда {command}: {verb}.");
                }
                else
                {
                    verb = command + "_ACCEPTED";
                    Log(baseDirectory, $"Принята команда {command}.");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(500);
                            if (command == "SLEEP")
                            {
                                if (!SetSuspendState(false, false, false)) Log(baseDirectory, "Windows не перешла в сон.");
                                return;
                            }
                            Process.Start(new ProcessStartInfo("shutdown.exe", command == "RESTART" ? "/r /t 10" : "/s /t 10")
                            { CreateNoWindow = true, UseShellExecute = false });
                        }
                        catch (Exception ex) { Log(baseDirectory, "Ошибка выполнения команды: " + ex.Message); }
                    });
                }
                var reply = SignedReply(key, verb, nonce);
                Ledger.Remember(nonce, command, endpoint, reply);
                await SendAsync(udp, reply, datagram.RemoteEndPoint);
            }
            finally { CommandGate.Release(); }
        }
        catch (Exception ex)
        {
            Log(baseDirectory, $"Ошибка обработки UDP-пакета: {ex.Message}");
        }
    }

    private static bool LaunchApplication(string baseDirectory, out string? error, string? profile = null)
    {
        error = null;
        var settings = ReadSettings(baseDirectory);
        var selected = profile is null ? null : settings.Profiles.GetValueOrDefault(profile);
        var path = (profile is null ? settings.LaunchPath : selected?.Path)?.Trim();
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "not_configured";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = (profile is null ? settings.LaunchArguments : selected?.Arguments) ?? "",
                WorkingDirectory = Path.GetDirectoryName(path) ?? baseDirectory,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task SendAsync(UdpClient udp, string text, IPEndPoint endpoint)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await udp.SendAsync(bytes, bytes.Length, endpoint);
    }

    private static void PruneChallenges()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var item in Challenges)
            if (item.Value.ExpiresAt < now) Challenges.TryRemove(item.Key, out _);
    }

    private static bool IsNonce(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    private static bool IsAllowedCommand(string command) =>
        command is "SHUTDOWN" or "RESTART" or "SLEEP" or "LAUNCH" or "PROFILE_WORK" or "PROFILE_GAME" or "PROFILE_CINEMA";

    private static string SignedReply(byte[] key, string verb, string nonce) =>
        $"WM1|{verb}|{nonce}|{Sign(key, $"{verb}|{nonce}")}";

    private static string Sign(byte[] key, string message)
    {
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static bool Verify(byte[] key, string message, string provided)
    {
        try
        {
            var expected = Convert.FromHexString(Sign(key, message));
            var actual = Convert.FromHexString(provided);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    private static string SettingsPath(string baseDirectory) => Path.Combine(baseDirectory, "wake-actions.json");

    private static AgentSettings ReadSettings(string baseDirectory)
    {
        try
        {
            return File.Exists(SettingsPath(baseDirectory))
                ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(SettingsPath(baseDirectory))) ?? new AgentSettings()
                : new AgentSettings();
        }
        catch (Exception ex)
        {
            Log(baseDirectory, $"Не удалось прочитать wake-actions.json: {ex.Message}");
            return new AgentSettings();
        }
    }

    private static void SaveSettings(string baseDirectory, AgentSettings settings) =>
        File.WriteAllText(SettingsPath(baseDirectory), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

    private static void Log(string baseDirectory, string message)
    {
        try
        {
            lock (LogLock)
                File.AppendAllText(Path.Combine(baseDirectory, "WakeMiniAgent.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static int InstallFirewallRule()
    {
        try
        {
            var command = new ProcessStartInfo("netsh.exe",
                "advfirewall firewall add rule name=\"Wake Mini Agent UDP 3000\" dir=in action=allow protocol=UDP localport=3000 profile=any")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            using var process = Process.Start(command);
            process!.WaitForExit();
            MessageBox.Show(process.ExitCode == 0 ? "Правило брандмауэра добавлено." : "Не удалось добавить правило брандмауэра.",
                "Wake Mini", MessageBoxButtons.OK, process.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось настроить Брандмауэр Windows: {ex.Message}", "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 4;
        }
    }

    private sealed record Challenge(long ExpiresAt, IPEndPoint RemoteEndPoint);

    private sealed class LaunchProfile
    {
        public string? Path { get; set; }
        public string? Arguments { get; set; }
    }

    private sealed class AgentSettings
    {
        public string? LaunchPath { get; set; }
        public string? LaunchArguments { get; set; }
        public bool LaunchOnStartup { get; set; }
        public string? Host { get; set; }
        public string? Mac { get; set; }
        public Dictionary<string, LaunchProfile> Profiles { get; set; } = new();
    }

    private sealed class AgentTrayContext : ApplicationContext
    {
        private readonly string _baseDirectory;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _launchNow;
        private readonly ToolStripMenuItem _autoLaunch;
        private bool _refreshingMenu;

        public AgentTrayContext(string baseDirectory)
        {
            _baseDirectory = baseDirectory;
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Wake Mini работает") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            var startup = new ToolStripMenuItem("Запускать помощник вместе с Windows") { CheckOnClick = true, Checked = StartupEnabled() };
            startup.CheckedChanged += (_, _) => { try { SetStartup(startup.Checked); } catch (Exception ex) { MessageBox.Show(ex.Message, "Wake Mini"); } };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripMenuItem("Подключить телефон / QR…", null, (_, _) => ConfigurePhone(_baseDirectory)));
            menu.Items.Add(new ToolStripMenuItem("Состояние и диагностика…", null, (_, _) => ShowDiagnostics(_baseDirectory)));
            menu.Items.Add(new ToolStripMenuItem("Профили: Работа / Игры / Кино…", null, (_, _) => ConfigureProfiles(_baseDirectory)));
            menu.Items.Add(new ToolStripMenuItem("Отменить выключение / перезагрузку", null, (_, _) => CancelShutdown()));
            menu.Items.Add(new ToolStripMenuItem("Настроить приложение…", null, (_, _) => ConfigureApplication()));
            _launchNow = new ToolStripMenuItem("Запустить приложение сейчас", null, (_, _) => LaunchFromTray());
            menu.Items.Add(_launchNow);
            _autoLaunch = new ToolStripMenuItem("Запускать приложение при входе в Windows") { CheckOnClick = true };
            _autoLaunch.CheckedChanged += (_, _) =>
            {
                if (!_refreshingMenu) SaveAutoLaunchSetting();
            };
            menu.Items.Add(_autoLaunch);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Открыть папку Wake Mini", null, (_, _) => OpenFolder()));
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitThread()));

            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
                Text = "Wake Mini — помощник работает",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (_, _) => ConfigureApplication();
            RefreshMenu();
            StartConfiguredAppIfNeeded();
        }

        private void RefreshMenu()
        {
            var settings = ReadSettings(_baseDirectory);
            _refreshingMenu = true;
            _autoLaunch.Checked = settings.LaunchOnStartup;
            _refreshingMenu = false;
            _launchNow.Enabled = !string.IsNullOrWhiteSpace(settings.LaunchPath) && File.Exists(settings.LaunchPath);
        }

        private void SaveAutoLaunchSetting()
        {
            var settings = ReadSettings(_baseDirectory);
            settings.LaunchOnStartup = _autoLaunch.Checked;
            if (settings.LaunchOnStartup && string.IsNullOrWhiteSpace(settings.LaunchPath))
            {
                _refreshingMenu = true;
                _autoLaunch.Checked = false;
                _refreshingMenu = false;
                MessageBox.Show("Сначала выберите приложение в настройках Wake Mini.", "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { SaveSettings(_baseDirectory, settings); }
            catch (Exception ex) { MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ConfigureApplication()
        {
            var settings = ReadSettings(_baseDirectory);
            using var form = new Form
            {
                Text = "Wake Mini — запуск приложения",
                AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new System.Drawing.Size(760, 400),
                MinimumSize = new System.Drawing.Size(620, 360),
                FormBorderStyle = FormBorderStyle.Sizable,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
                AutoScroll = true
            };

            // Use content-sized rows: fixed pixel heights clip controls on scaled Windows displays.
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 1,
                RowCount = 5,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < layout.RowCount; row++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var pathRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var pathLabel = new Label
            {
                Text = "Приложение (.exe):",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 12, 8)
            };
            var pathBox = new TextBox
            {
                Text = settings.LaunchPath ?? "",
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 8, 8, 8)
            };
            var browse = new Button
            {
                Text = "Обзор…",
                AutoSize = true,
                MinimumSize = new System.Drawing.Size(100, 40),
                Anchor = AnchorStyles.Right,
                Margin = new Padding(3, 4, 3, 4)
            };
            browse.Click += (_, _) =>
            {
                using var picker = new OpenFileDialog
                {
                    Filter = "Программы Windows (*.exe)|*.exe",
                    CheckFileExists = true
                };
                if (picker.ShowDialog(form) == DialogResult.OK) pathBox.Text = picker.FileName;
            };
            pathRow.Controls.Add(pathLabel, 0, 0);
            pathRow.Controls.Add(pathBox, 1, 0);
            pathRow.Controls.Add(browse, 2, 0);

            var argsLabel = new Label
            {
                Text = "Параметры запуска (необязательно):",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 2)
            };
            var argsBox = new TextBox
            {
                Text = settings.LaunchArguments ?? "",
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 4, 3, 10)
            };
            var autoStart = new CheckBox
            {
                Text = "Запускать это приложение при входе в Windows",
                Checked = settings.LaunchOnStartup,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 8)
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 8, 0, 0)
            };
            var save = new Button
            {
                Text = "Сохранить",
                DialogResult = DialogResult.OK,
                MinimumSize = new System.Drawing.Size(110, 40),
                AutoSize = true,
                Margin = new Padding(8, 3, 0, 3)
            };
            var cancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                MinimumSize = new System.Drawing.Size(100, 40),
                AutoSize = true,
                Margin = new Padding(8, 3, 0, 3)
            };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);

            layout.Controls.Add(pathRow, 0, 0);
            layout.Controls.Add(argsLabel, 0, 1);
            layout.Controls.Add(argsBox, 0, 2);
            layout.Controls.Add(autoStart, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            form.Controls.Add(layout);
            form.AcceptButton = save;
            form.CancelButton = cancel;

            if (form.ShowDialog() != DialogResult.OK) return;
            var path = pathBox.Text.Trim();
            if (path.Length > 0 && (!Path.IsPathFullyQualified(path) || !File.Exists(path)
                || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Выберите существующий файл .exe.", "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (autoStart.Checked && path.Length == 0)
            {
                MessageBox.Show("Для автозапуска сначала выберите приложение.", "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            settings.LaunchPath = path.Length == 0 ? null : Path.GetFullPath(path);
            settings.LaunchArguments = argsBox.Text;
            settings.LaunchOnStartup = autoStart.Checked;
            try
            {
                SaveSettings(_baseDirectory, settings);
                RefreshMenu();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LaunchFromTray()
        {
            if (!LaunchApplication(_baseDirectory, out var error))
                MessageBox.Show(error == "not_configured" ? "Сначала выберите приложение в настройках Wake Mini." : $"Не удалось запустить приложение: {error}",
                    "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void StartConfiguredAppIfNeeded()
        {
            var settings = ReadSettings(_baseDirectory);
            if (!settings.LaunchOnStartup) return;
            var timer = new System.Windows.Forms.Timer { Interval = 2500 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!LaunchApplication(_baseDirectory, out var error))
                {
                    Log(_baseDirectory, error == "not_configured" ? "Автозапуск пропущен: приложение не настроено." : $"Ошибка автозапуска: {error}");
                    _tray.ShowBalloonTip(4000, "Wake Mini", "Не удалось запустить выбранное приложение. Проверьте настройки.", ToolTipIcon.Warning);
                }
            };
            timer.Start();
        }

        private void OpenFolder()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", _baseDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Wake Mini", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        protected override void ExitThreadCore()
        {
            _tray.Visible = false;
            _tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
