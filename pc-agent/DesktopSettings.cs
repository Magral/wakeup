using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using QRCoder;

internal static partial class Program
{
    private static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("WakeMiniAgent") is string value && value == $"\"{Environment.ProcessPath}\"";
    }
    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("WakeMiniAgent", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("WakeMiniAgent", false);
    }
    private static (Form Form, TableLayoutPanel Grid) SettingsWindow(string title)
    {
        var form = new Form { Text = "Wake Mini — " + title, AutoScaleMode = AutoScaleMode.Dpi,
            Size = new(800, 650), MinimumSize = new(620, 460), StartPosition = FormStartPosition.CenterScreen };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(grid); form.Controls.Add(scroll);
        return (form, grid);
    }
    private static T Row<T>(TableLayoutPanel grid, T control) where T : Control
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(4, 6, 4, 6);
        control.Dock = DockStyle.Top;
        grid.Controls.Add(control, 0, row);
        return control;
    }
    private static TextBox Field(TableLayoutPanel grid, string label, string? value)
    {
        Row(grid, new Label { Text = label, AutoSize = true });
        return Row(grid, new TextBox { Text = value ?? "" });
    }
    private static void ConfigurePhone(string directory)
    {
        var settings = ReadSettings(directory);
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up
            && x.NetworkInterfaceType == NetworkInterfaceType.Ethernet && x.GetPhysicalAddress().GetAddressBytes().Length == 6);
        var pair = SettingsWindow("подключение телефона");
        using var form = pair.Form;
        var grid = pair.Grid;
        Row(grid, new Label { AutoSize = true, Text = "QR содержит секретный ключ. Сканируйте его только своим телефоном." });
        var host = Field(grid, "DDNS или IP (для управления через интернет укажите DDNS роутера)", settings.Host);
        var mac = Field(grid, "MAC проводного адаптера", settings.Mac ?? adapter?.GetPhysicalAddress().ToString());
        var show = Row(grid, new Button { Text = "Сохранить и показать QR-код", AutoSize = true });
        var picture = Row(grid, new PictureBox { Height = 340, SizeMode = PictureBoxSizeMode.Zoom });
        show.Click += (_, _) =>
        {
            var normalized = mac.Text.Replace(":", "").Replace("-", "").Trim();
            if (string.IsNullOrWhiteSpace(host.Text) || host.Text.Contains("://") || host.Text.Any(char.IsWhiteSpace)
                || normalized.Length != 12 || !normalized.All(Uri.IsHexDigit))
            { MessageBox.Show("Введите корректные DDNS/IP без https:// и MAC-адрес."); return; }
            settings.Host = host.Text.Trim(); settings.Mac = normalized;
            try
            {
                SaveSettings(directory, settings);
                var payload = JsonSerializer.Serialize(new { type = "wakemini", version = 1, host = settings.Host,
                    mac = normalized, port = 3000, key = Convert.ToHexString(LoadOrCreateKey(directory)) });
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
                using var code = new QRCode(data);
                var old = picture.Image;
                picture.Image = code.GetGraphic(6);
                old?.Dispose();
            }
            catch (Exception ex) { MessageBox.Show("Не удалось создать QR: " + ex.Message); }
        };
        form.FormClosed += (_, _) => picture.Image?.Dispose();
        form.ShowDialog();
    }
    private static void ShowDiagnostics(string directory)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString());
        var log = Path.Combine(directory, "WakeMiniAgent.log");
        var text = "Wake Mini " + typeof(Program).Assembly.GetName().Version + "\r\nUDP-порт: 3000\r\nIP: " + string.Join(", ", addresses)
            + "\r\nПоследняя принятая команда: " + (LastRequest?.ToString() ?? "ещё нет")
            + "\r\n\r\n" + (File.Exists(log) ? string.Join("\r\n", File.ReadLines(log).TakeLast(40)) : "Журнал пока пуст.");
        var pair = SettingsWindow("диагностика"); using var form = pair.Form;
        Row(pair.Grid, new TextBox { Text = text, Multiline = true, ReadOnly = true, Height = 420, ScrollBars = ScrollBars.Vertical });
        var copy = Row(pair.Grid, new Button { Text = "Скопировать диагностику", AutoSize = true });
        copy.Click += (_, _) => Clipboard.SetText(text);
        form.ShowDialog();
    }
    private static void ConfigureProfiles(string directory)
    {
        var settings = ReadSettings(directory);
        var pair = SettingsWindow("профили запуска"); using var form = pair.Form;
        var inputs = new Dictionary<string, (TextBox Path, TextBox Args)>();
        foreach (var (id, title) in new[] { ("work", "Работа"), ("game", "Игры"), ("cinema", "Кино") })
        {
            var profile = settings.Profiles.GetValueOrDefault(id);
            var path = Field(pair.Grid, title + " — программа (.exe)", profile?.Path);
            var browse = Row(pair.Grid, new Button { Text = "Выбрать программу…", AutoSize = true });
            browse.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe" }; if (picker.ShowDialog(form) == DialogResult.OK) path.Text = picker.FileName; };
            inputs[id] = (path, Field(pair.Grid, "Параметры запуска", profile?.Arguments));
        }
        var save = Row(pair.Grid, new Button { Text = "Сохранить профили", AutoSize = true });
        save.Click += (_, _) =>
        {
            foreach (var input in inputs.Values)
                if (input.Path.Text.Length > 0 && (!Path.IsPathFullyQualified(input.Path.Text) || !File.Exists(input.Path.Text)
                    || !input.Path.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                { MessageBox.Show("Выберите существующий файл .exe или оставьте профиль пустым."); return; }
            foreach (var (id, input) in inputs) settings.Profiles[id] = new LaunchProfile { Path = input.Path.Text, Arguments = input.Args.Text };
            try { SaveSettings(directory, settings); form.Close(); } catch (Exception ex) { MessageBox.Show(ex.Message); }
        };
        form.ShowDialog();
    }
    private static void CancelShutdown()
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Wake Mini"); }
    }
}
