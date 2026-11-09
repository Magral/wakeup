// Shared with regression tests; all flags are Win32 window style values.
internal static class DesktopWindowPolicy
{
    internal static bool ShouldMinimize(long style, long extendedStyle, string processName)
    {
        const long child = 0x40000000, minimizeBox = 0x00020000;
        const long toolWindow = 0x80, noActivate = 0x08000000, layered = 0x80000;
        if ((style & child) != 0 || (style & minimizeBox) == 0) return false;
        if ((extendedStyle & (toolWindow | noActivate | layered)) != 0) return false;
        // Explicit protection also covers overlay versions with ordinary styles.
        return !processName.StartsWith("NVIDIA", System.StringComparison.OrdinalIgnoreCase)
            && !processName.StartsWith("nvsp", System.StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("steam", System.StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("steamwebhelper", System.StringComparison.OrdinalIgnoreCase);
    }
}
