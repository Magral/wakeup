// Persist only a non-secret identity hash, not credentials. A helper restart is not a PC boot.
internal static class PowerStartupMarker
{
    internal static bool Record(string path, string session, bool startupLaunch)
    {
        var previous = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        File.WriteAllText(path + ".tmp", session);
        File.Move(path + ".tmp", path, true);
        return previous == null ? startupLaunch : previous != session;
    }
}
