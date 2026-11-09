using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class Program
{
    private static int DesktopBusy;
    private static string DesktopMessage = "Переключение Работа / Игры ещё не запускалось.";
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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

    private static int MinimizeProgramWindows(params string[] names)
    {
        var processIds = new HashSet<uint>();
        foreach (var name in names)
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try { processIds.Add((uint)process.Id); }
                catch (InvalidOperationException) { }
            }
        }

        var minimized = 0;
        EnumWindowsProc callback = (window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains(processId) && ShowWindowAsync(window, 6)) // SW_MINIMIZE
                minimized++;
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return minimized;
    }

    private static int MinimizeOtherWindows()
    {
        var steamIds = new HashSet<uint>();
        foreach (var name in new[] { "steam", "steamwebhelper" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) { steamIds.Add((uint)process.Id); }
        var count = 0;
        EnumWindowsProc callback = (window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)
                || window == GetShellWindow() || window == GetDesktopWindow()) return true;
            var className = new System.Text.StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return true;
            GetWindowThreadProcessId(window, out var id);
            if (steamIds.Contains(id)) return true;
            // Only ordinary app windows: tool/no-activate/layered windows include
            // performance HUDs. Never minimize them, even when visible.
            var style = GetWindowLongPtr(window, -16).ToInt64();
            var extendedStyle = GetWindowLongPtr(window, -20).ToInt64();
            string processName;
            try { using var process = Process.GetProcessById((int)id); processName = process.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return true; } // Unknown owner: leave the window alone.
            if (DesktopWindowPolicy.ShouldMinimize(style, extendedStyle, processName)
                && ShowWindowAsync(window, 6)) count++;
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return count;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    private static string DetectSteam() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe")
    }.FirstOrDefault(File.Exists) ?? "";

    private static string FindRunningSteamPath()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch (Exception) { }
            }
        }
        return DetectSteam();
    }

    private static bool IsProgramRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool StartDesktopMode(string directory, string profile, out string? error)
    {
        error = null;
        var settings = ReadSettings(directory);
        var path = settings.Profiles.GetValueOrDefault(profile)?.Path ?? "";
        var expected = profile == "work" ? "chrome.exe" : "steam.exe";
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase))
        { error = "not_configured"; return false; }
        lock (TvLock)
        {
            if (ChatOperation != null || Interlocked.CompareExchange(ref DesktopBusy, 1, 0) != 0)
            { error = "busy"; return false; }
        }
        DesktopMessage = "Запрос переключения принят…";
        _ = Task.Run(async () =>
        {
            try
            {
                if (profile == "work")
                {
                    var steamWasRunning = IsProgramRunning("steam");
                    var steamPath = steamWasRunning ? FindRunningSteamPath() : "";
                    if (steamWasRunning && !string.IsNullOrWhiteSpace(steamPath))
                    {
                        Process.Start(new ProcessStartInfo(steamPath, "steam://close/bigpicture") { UseShellExecute = true });
                        await Task.Delay(500);
                    }
                    var steamWindowsMinimized = MinimizeProgramWindows("steam", "steamwebhelper");
                    if (!HasProgramWindow("chrome"))
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

                    bool focused = false;
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        if (BringProgramForward("chrome")) { focused = true; break; }
                        await Task.Delay(250);
                    }
                    DesktopMessage = focused
                        ? $"Big Picture закрыт по запросу; окна Steam свёрнуты ({steamWindowsMinimized}), Chrome выведен на передний план."
                        : "Big Picture закрыт по запросу; окна Steam свёрнуты. Chrome запущен, но Windows не подтвердила перевод окна на передний план.";
                }
                else
                {
                    var chromeWindowsMinimized = MinimizeOtherWindows();
                    Process.Start(new ProcessStartInfo(path, "steam://open/bigpicture") { UseShellExecute = true });
                    bool focused = false;
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        if (BringProgramForward("steam", "steamwebhelper")) { focused = true; break; }
                        await Task.Delay(250);
                    }
                    DesktopMessage = focused
                        ? $"Окна остальных программ свёрнуты ({chromeWindowsMinimized}); запрос Big Picture отправлен, Steam выведен на передний план."
                        : $"Окна остальных программ свёрнуты ({chromeWindowsMinimized}); запрос Big Picture отправлен, но Windows не подтвердила перевод окна Steam на передний план.";
                }
            }
            catch (Exception ex) { DesktopMessage = "Переключение не завершено: " + ex.Message; }
            finally { Log(directory, "Профиль: " + DesktopMessage); Interlocked.Exchange(ref DesktopBusy, 0); }
        });
        return true;
    }
}
