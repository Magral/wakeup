// Mixer levels, never the master device volume. Pure policy for Windows session adapters.
internal sealed record GameAudioSession(string Key, string ProcessKey, float Volume);
internal sealed record GameAudioVolume(string Key, string ProcessKey, float Original, float Applied);
internal sealed record GameAudioChange(string Key, float Target);
internal sealed class GameAudioDuckingState
{
    private readonly Dictionary<string, GameAudioVolume> volumes;
    internal GameAudioDuckingState(IEnumerable<GameAudioVolume>? saved = null) => volumes =
        (saved ?? []).Where(x => float.IsFinite(x.Original) && x.Original is >= 0 and <= 1
            && float.IsFinite(x.Applied) && x.Applied is >= 0 and <= 1)
        .DistinctBy(x => x.Key).ToDictionary(x => x.Key);
    internal IReadOnlyList<GameAudioVolume> Entries => volumes.Values.ToArray();
    internal GameAudioChange? Duck(GameAudioSession session)
    {
        if (volumes.ContainsKey(session.Key) || !float.IsFinite(session.Volume) || session.Volume is < 0 or > 1) return null;
        // Windows can carry an already attenuated slider to a new render endpoint.
        var migrated = volumes.Values.FirstOrDefault(x => x.ProcessKey == session.ProcessKey && Same(session.Volume, x.Applied));
        var original = migrated?.Original ?? session.Volume;
        var item = new GameAudioVolume(session.Key, session.ProcessKey, original, original * .5f);
        volumes.Add(item.Key, item);
        return new(item.Key, item.Applied);
    }
    internal GameAudioChange? Restore(GameAudioSession session) => volumes.TryGetValue(session.Key, out var item)
        && Same(session.Volume, item.Applied) ? new(item.Key, item.Original) : null;
    internal void Forget(string key) => volumes.Remove(key);
    private static bool Same(float a, float b) => Math.Abs(a - b) < .0001f;
    internal static bool IsGamePath(string path, IEnumerable<string> roots) => roots.Any(root =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
}
