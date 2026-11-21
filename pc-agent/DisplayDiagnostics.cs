using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Read-only snapshot of the Windows display topology. This deliberately does not
// change display modes or reset the NVIDIA driver: diagnostics must never disrupt
// a working desktop while collecting evidence after an HDMI wake/resume.
internal static class DisplayDiagnostics
{
    private const uint AttachedToDesktop = 0x00000001;
    private const uint PrimaryDevice = 0x00000004;
    private const uint MirroringDriver = 0x00000008;
    private const uint RemoteDevice = 0x04000000;
    private const uint Disconnect = 0x02000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber,
        ref DisplayDevice displayDevice, uint flags);

    private static DisplayDevice CreateDevice() => new()
    {
        Size = Marshal.SizeOf<DisplayDevice>(),
        DeviceName = string.Empty,
        DeviceString = string.Empty,
        DeviceId = string.Empty,
        DeviceKey = string.Empty
    };

    internal static string Capture()
    {
        var report = new StringBuilder();
        report.AppendLine("Диагностика видеовыходов Windows (только чтение)");
        report.AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            var screens = Screen.AllScreens;
            report.AppendLine($"Активных экранов Windows: {screens.Length}");
            foreach (var screen in screens)
                report.AppendLine($"Экран: {screen.DeviceName}; {screen.Bounds.Width}x{screen.Bounds.Height}; " +
                    $"основной: {(screen.Primary ? "да" : "нет")}");

            var adaptersFound = false;
            for (uint adapterIndex = 0; adapterIndex < 32; adapterIndex++)
            {
                var adapter = CreateDevice();
                if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0)) break;
                adaptersFound = true;
                report.AppendLine($"Видеоадаптер: {adapter.DeviceString}; имя: {adapter.DeviceName}; " +
                    $"состояние: {FormatFlags(adapter.StateFlags)}");

                var monitorFound = false;
                for (uint monitorIndex = 0; monitorIndex < 16; monitorIndex++)
                {
                    var monitor = CreateDevice();
                    if (!EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 0)) break;
                    monitorFound = true;
                    var id = SafeModelId(monitor.DeviceId);
                    report.AppendLine($"  Монитор: {monitor.DeviceString}; состояние: {FormatFlags(monitor.StateFlags)}" +
                        (id.Length == 0 ? string.Empty : $"; ID: {id}"));
                }
                if (!monitorFound) report.AppendLine("  Монитор: Windows не перечисляет дочерние дисплеи этого адаптера.");
            }
            if (!adaptersFound) report.AppendLine("Windows не перечислила видеоадаптеры через EnumDisplayDevices.");
        }
        catch (Exception ex)
        {
            report.AppendLine("Ошибка чтения состояния дисплеев: " + ex.Message);
        }
        report.Append("Подсказка: отсутствие LG в активных экранах означает, что Windows сейчас не считает его подключённым дисплеем. Наличие LG при чёрном экране указывает, что обнаружение прошло, но не доказывает наличие видеосигнала.");
        return report.ToString();
    }

    private static string FormatFlags(uint flags)
    {
        var values = new List<string>();
        if ((flags & AttachedToDesktop) != 0) values.Add("подключён к рабочему столу");
        if ((flags & PrimaryDevice) != 0) values.Add("основной");
        if ((flags & MirroringDriver) != 0) values.Add("зеркальный драйвер");
        if ((flags & RemoteDevice) != 0) values.Add("удалённый");
        if ((flags & Disconnect) != 0) values.Add("отключён");
        return values.Count == 0 ? $"флаги 0x{flags:X8}" : string.Join(", ", values);
    }

    // Keep the monitor model/vendor portion but omit the per-unit instance/serial.
    private static string SafeModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var parts = value.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\\', parts.Take(Math.Min(2, parts.Length)));
    }
}
