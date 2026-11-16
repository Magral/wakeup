// Mixer levels, never the master device volume. Pure policy for Windows session adapters.
internal sealed record GameAudioSession(string Key, string ProcessKey, float Volume,
    string RecoveryKey = "", string Endpoint = "", bool Active = true);
internal sealed record GameAudioVolume(string Key, string ProcessKey, float Original, float Applied,
    string RecoveryKey = "", string Endpoint = "", bool UserChanged = false);
internal sealed record GameAudioChange(string Key, float Target);
internal sealed class GameAudioDuckingState
{
    private readonly Dictionary<string, GameAudioVolume> volumes;
    private readonly HashSet<string> recoverable;
    internal GameAudioDuckingState(IEnumerable<GameAudioVolume>? saved = null)
    {
        volumes = (saved ?? []).Where(x => float.IsFinite(x.Original) && x.Original is >= 0 and <= 1
            && float.IsFinite(x.Applied) && x.Applied is >= 0 and <= 1)
            .DistinctBy(x => x.Key).ToDictionary(x => x.Key);
        recoverable = volumes.Keys.ToHashSet();
    }
    internal IReadOnlyList<GameAudioVolume> Entries => volumes.Values.ToArray();
    internal bool Observe(GameAudioSession session)
    {
        if (!volumes.TryGetValue(session.Key, out var item) || Same(session.Volume, item.Applied)) return false;
        volumes[session.Key] = item with { Original = session.Volume, Applied = session.Volume, UserChanged = true };
        return true;
    }
    internal GameAudioChange? Duck(GameAudioSession session, ISet<string>? activeKeys = null)
    {
        if (volumes.ContainsKey(session.Key)) { Observe(session); return null; }
        if (!session.Active || !float.IsFinite(session.Volume) || session.Volume is < 0 or > 1) return null;
        // Infer transfer only after the prior session ceased rendering on another endpoint.
        // Concurrent sessions on one endpoint always keep their independent baselines.
        var migrated = activeKeys == null || session.Endpoint.Length == 0 ? null : volumes.Values.FirstOrDefault(x =>
            x.ProcessKey == session.ProcessKey && x.Endpoint.Length > 0 && x.Endpoint != session.Endpoint
            && !activeKeys.Contains(x.Key) && Same(session.Volume, x.Applied));
        var original = migrated?.Original ?? session.Volume;
        var manual = migrated?.UserChanged ?? false;
        var item = new GameAudioVolume(session.Key, session.ProcessKey, original, manual ? original : original * .5f,
            session.RecoveryKey, session.Endpoint, manual);
        volumes.Add(item.Key, item);
        return new(item.Key, item.Applied);
    }
    internal GameAudioVolume? FindForRestore(GameAudioSession session, ISet<string>? presentKeys = null)
    {
        if (volumes.TryGetValue(session.Key, out var item)) return item;
        // Only journal entries loaded after a restart may match a new instance, and never
        // while the original instance is still present elsewhere in the enumeration.
        return presentKeys == null || session.RecoveryKey.Length == 0 ? null : volumes.Values.FirstOrDefault(x =>
            recoverable.Contains(x.Key) && !presentKeys.Contains(x.Key) && x.RecoveryKey == session.RecoveryKey
            && Same(session.Volume, x.Applied));
    }
    internal GameAudioChange? Restore(GameAudioSession session, ISet<string>? presentKeys = null)
    {
        var item = FindForRestore(session, presentKeys);
        return item != null && Same(session.Volume, item.Applied) ? new(item.Key, item.Original) : null;
    }
    internal void Forget(string key) { volumes.Remove(key); recoverable.Remove(key); }
    private static bool Same(float a, float b) => Math.Abs(a - b) < .0001f;
    internal static bool IsGamePath(string path, IEnumerable<string> roots) => roots.Any(root =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
}
