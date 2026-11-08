using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class Program
{
    private static int DesktopBusy;
    private static string DesktopMessage = "Переключение Работа / Игры ещё не запускалось.";
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);

    private static bool BringProgramForward(params string[] names)
    {
        foreach (var name in names)
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
                    SetForegroundWindow(window);
                    if (GetForegroundWindow() == window) return true;
                }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }
    private static bool HasProgramWindow(string name)
    {
        var found = false;
        foreach (var process in Process.GetProcessesByName(name))
            using (process) { try { found |= process.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { } }
        return found;
    }
    private static string DetectSteam() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe")
    }.FirstOrDefault(File.Exists) ?? "";

    private static bool StartDesktopMode(string directory, string profile, out string? error)
    {
        error = null;
        var settings = ReadSettings(directory);
        var path = settings.Profiles.GetValueOrDefault(profile)?.Path ?? "";
        var expected = profile == "work" ? "chrome.exe" : "steam.exe";
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase))
        { error = "not_configured"; return false; }
        if (Interlocked.CompareExchange(ref DesktopBusy, 1, 0) != 0)
        { error = "busy"; return false; }
        DesktopMessage = "Запрос переключения принят…";
        _ = Task.Run(async () =>
        {
            try
            {
                if (profile == "game")
                    Process.Start(new ProcessStartInfo(path, "steam://open/bigpicture") { UseShellExecute = true });
                else if (!HasProgramWindow("chrome"))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                bool focused = false;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    if (profile == "work" ? BringProgramForward("chrome") : BringProgramForward("steam", "steamwebhelper"))
                    { focused = true; break; }
                    await Task.Delay(250);
                }
                DesktopMessage = focused
                    ? profile == "work" ? "Chrome выведен на передний план; Steam и игры не закрывались."
                        : "Запрос Big Picture отправлен, окно Steam выведено на передний план."
                    : "Запрос запуска отправлен, но Windows не подтвердила перевод окна на передний план. Выберите окно вручную.";
            }
            catch (Exception ex) { DesktopMessage = "Переключение не завершено: " + ex.Message; }
            finally { Log(directory, "Профиль: " + DesktopMessage); Interlocked.Exchange(ref DesktopBusy, 0); }
        });
        return true;
    }
}
