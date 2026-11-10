using System.Text.Json;
using System.Windows.Forms;

internal static partial class Program
{
    private static readonly object TvLock = new();
    private static CancellationTokenSource? TvOperation;
    private static string TvState = "IDLE";
    private static string TvMessage = "Команд телевизору ещё нет.";
    private static string TvPath(string directory) => Path.Combine(directory, "wake-tv.json");

    private static LgTvSettings ReadTvSettings(string directory) => File.Exists(TvPath(directory))
        ? JsonSerializer.Deserialize<LgTvSettings>(File.ReadAllText(TvPath(directory))) ?? new() : new();

    private static void SaveTvSettings(string directory, LgTvSettings settings)
    {
        var path = TvPath(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch { }
    }

    private static string StartTv(string directory, int? input = null)
    {
        var verb = input.HasValue ? "TV_HDMI" + input.Value : "TV_PC";
        lock (TvLock)
        {
            if (TvOperation != null || ChatOperation != null || Volatile.Read(ref DesktopBusy) != 0) return verb + "_BUSY";
            LgTvSettings settings;
            try
            {
                settings = ReadTvSettings(directory);
                if (input.HasValue) settings.Hdmi = input.Value;
                settings.Validate();
                if (string.IsNullOrWhiteSpace(settings.ClientKey)) return verb + "_NOT_CONFIGURED";
            }
            catch { return verb + "_NOT_CONFIGURED"; }
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            TvOperation = cancellation;
            TvState = "BUSY";
            TvMessage = "Включаю телевизор и выбираю HDMI…";
            _ = Task.Run(async () =>
            {
                string state, message;
                try
                {
                    await new LgWebOsClient(settings, _ => { }).ShowPcAsync(cancellation.Token);
                    state = "OK"; message = $"Телевизор подтвердил HDMI {settings.Hdmi}.";
                }
                catch (OperationCanceledException)
                { state = "FAILED"; message = "Управление ТВ отменено или истекло время ожидания."; }
                catch (Exception ex) { state = "FAILED"; message = ex.Message; }
                lock (TvLock)
                {
                    TvState = state; TvMessage = message;
                    TvOperation = null;
                    cancellation.Dispose();
                }
                Log(directory, "LG TV: " + message);
            });
            return verb + "_ACCEPTED";
        }
    }

    private static string TvStatus()
    {
        lock (TvLock) return "TV_STATUS_" + TvState;
    }

    private static void CancelTv()
    {
        lock (TvLock) TvOperation?.Cancel();
    }

    private static void StartTvOnLogin(string directory)
    {
        try
        {
            if (!ReadTvSettings(directory).OnStartup) return;
            var result = StartTv(directory);
            Log(directory, "LG TV при запуске помощника: " + result);
        }
        catch (Exception ex) { Log(directory, "LG TV: " + ex.Message); }
    }

    private static void ConfigureTv(string directory)
    {
        LgTvSettings settings;
        try { settings = ReadTvSettings(directory); }
        catch (Exception ex) { MessageBox.Show("Не удалось прочитать настройки ТВ: " + ex.Message); return; }
        var window = SettingsWindow("телевизор LG OLED");
        using var form = window.Form;
        var grid = window.Grid;
        Row(grid, new Label { AutoSize = true, MaximumSize = new(700, 0), Text =
            "Включите ТВ с пульта. Включите «TV On With Mobile / Включение через Wi-Fi» даже при подключении кабелем. " +
            "Закрепите IP телевизора в роутере. ПК и ТВ должны быть в одной локальной сети." });
        var address = Field(grid, "Локальный IPv4-адрес телевизора", settings.Address);
        var mac = Field(grid, "MAC телевизора (текущего подключения Wi-Fi или Ethernet)", settings.Mac);
        Row(grid, new Label { Text = "HDMI компьютера", AutoSize = true });
        var hdmi = Row(grid, new NumericUpDown { Minimum = 1, Maximum = 4, Value = Math.Clamp(settings.Hdmi, 1, 4) });
        var secure = Row(grid, new CheckBox { Text = "Защищённое соединение WSS, порт 3001 (для C5)", AutoSize = true, Checked = settings.Secure });
        var auto = Row(grid, new CheckBox { Text = "Включать ТВ и выбирать HDMI при запуске помощника Windows", AutoSize = true, Checked = settings.OnStartup });
        Row(grid, new Label { AutoSize = true, ForeColor = System.Drawing.Color.DarkRed, MaximumSize = new(700, 0), Text =
            "Если включить автозапуск ТВ, он сработает и после удалённого включения ПК при входе в Windows. " +
            "Для запуска без телевизора отключите эту настройку и используйте кнопку «ПК + телевизор» на телефоне. " +
            "Выключение ПК не выключает телевизор." });
        var pair = Row(grid, new Button { Text = "Сохранить и выполнить повторное сопряжение с ТВ…", AutoSize = true });
        var save = Row(grid, new Button { Text = "Сохранить настройки", AutoSize = true });
        var test = Row(grid, new Button { Text = "Включить ТВ / переключить на ПК", AutoSize = true });
        var cancel = Row(grid, new Button { Text = "Прекратить попытки включения ТВ", AutoSize = true });
        string message;
        lock (TvLock) message = TvMessage;
        var status = Row(grid, new Label { Text = message, AutoSize = true, MaximumSize = new(700, 0) });
        using var refresh = new System.Windows.Forms.Timer { Interval = 500 };
        var lastMessage = message;
        refresh.Tick += (_, _) =>
        {
            if (!pair.Enabled || form.IsDisposed) return;
            string current;
            lock (TvLock) current = TvMessage;
            if (current != lastMessage) { lastMessage = current; status.Text = current; }
        };
        refresh.Start();
        using var lifetime = new CancellationTokenSource();
        form.FormClosing += (_, _) => lifetime.Cancel();
        LgTvSettings Capture()
        {
            var updated = new LgTvSettings { Address = address.Text.Trim(), Mac = mac.Text.Trim(), Hdmi = (int)hdmi.Value,
                Secure = secure.Checked, OnStartup = auto.Checked };
            updated.Validate();
            // A changed TV/transport requires explicit pairing. Changing only HDMI does not.
            if (updated.Address == settings.Address && updated.Mac == settings.Mac && updated.Secure == settings.Secure)
            { updated.ClientKey = settings.ClientKey; updated.CertificateHash = settings.CertificateHash; }
            return updated;
        }
        bool Persist()
        {
            try
            {
                var updated = Capture();
                if (updated.OnStartup && string.IsNullOrWhiteSpace(updated.ClientKey))
                    throw new InvalidOperationException("Сначала выполните сопряжение с ТВ.");
                SaveTvSettings(directory, updated); settings = updated;
                return true;
            }
            catch (Exception ex) { status.Text = ex.Message; return false; }
        }
        save.Click += (_, _) => { if (Persist()) status.Text = "Настройки сохранены."; };
        test.Click += (_, _) =>
        {
            if (!Persist()) return;
            status.Text = StartTv(directory) switch
            {
                "TV_PC_ACCEPTED" => "Включаю ТВ и выбираю HDMI. Результат — в диагностике Wake Mini.",
                "TV_PC_BUSY" => "Управление телевизором уже выполняется. Результат — в диагностике Wake Mini.",
                _ => "Сначала заполните настройки и выполните сопряжение с телевизором."
            };
        };
        cancel.Click += (_, _) => { CancelTv(); status.Text = "Попытки остановлены. Уже выполненное включение/переключение не отменяется."; };
        pair.Click += async (_, _) =>
        {
            lock (TvLock)
                if (TvOperation != null || ChatOperation != null) { status.Text = "Сначала остановите текущую команду ТВ или подготовку разговора."; return; }
            pair.Enabled = save.Enabled = test.Enabled = false;
            try
            {
                var updated = Capture();
                status.Text = "Подтвердите разрешение управления на экране телевизора (ожидание до 60 секунд)…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await new LgWebOsClient(updated, value => SaveTvSettings(directory, value)).PairAsync(timeout.Token);
                settings = updated;
                Log(directory, "LG TV: новое сопряжение сохранено, разрешён выбор HDMI и запуск HDMI-входа.");
                if (!form.IsDisposed) status.Text = "Сопряжение сохранено. Можно проверить включение и HDMI.";
            }
            catch (OperationCanceledException) { if (!form.IsDisposed) status.Text = "Сопряжение отменено или истекло время ожидания."; }
            catch (Exception ex) { Log(directory, "LG TV сопряжение: " + ex.Message); if (!form.IsDisposed) status.Text = ex.Message; }
            finally { if (!form.IsDisposed) pair.Enabled = save.Enabled = test.Enabled = true; }
        };
        form.ShowDialog();
    }
}
