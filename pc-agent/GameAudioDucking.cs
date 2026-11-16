using System.Text.Json;

// One serialized owner for all temporary mixer changes. No device/master volume changes.
internal sealed class GameAudioDucking
{
    private readonly object gate = new();
    private readonly string journal;
    private readonly Action<string> log;
    private readonly Action<Action<GameAudioSession, string, Action<float>>> visit;
    private GameAudioDuckingState state;
    private string[] gameRoots = [];
    private bool active;
    private string lastError = "";
    internal GameAudioDucking(string directory, Action<string> log, Action<Action<GameAudioSession, string, Action<float>>> visit)
    {
        journal = Path.Combine(directory, "wake-audio-ducking.json");
        this.log = log;
        this.visit = visit;
        state = new();
        try
        {
            if (File.Exists(journal)) state = new(JsonSerializer.Deserialize<List<GameAudioVolume>>(File.ReadAllText(journal)));
        }
        catch (Exception ex) { log("Не удалось прочитать сохранённую громкость игр: " + ex.Message); }
    }
    internal bool Begin(IEnumerable<string> roots)
    {
        lock (gate)
        {
            if (active) return false;
            Refresh(); // First recover any changes left by a previous interrupted conversation.
            gameRoots = roots.ToArray();
            active = true;
            try { Refresh(); }
            catch { active = false; RefreshSafely(); throw; }
            log("Атлас: приглушение игр Steam на 50% включено.");
            return true;
        }
    }
    internal void End()
    {
        lock (gate)
        {
            var wasActive = active;
            active = false;
            RefreshSafely();
            if (wasActive) log("Атлас: приглушение игр снято; исходная громкость восстановлена для доступных аудиосеансов.");
        }
    }
    internal async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                lock (gate) RefreshSafely();
                await Task.Delay(750, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { End(); }
    }
    private void RefreshSafely()
    {
        try { Refresh(); lastError = ""; }
        catch (Exception ex)
        {
            if (lastError != ex.Message) { lastError = ex.Message; log("Громкость игр: " + ex.Message); }
        }
    }
    private void Refresh()
    {
        if (!active && state.Entries.Count == 0) return;
        visit((session, path, set) =>
        {
            if (active)
            {
                if (!GameAudioDuckingState.IsGamePath(path, gameRoots)) return;
                var change = state.Duck(session);
                if (change == null) return;
                // The journal reaches disk BEFORE Windows persists the lowered slider.
                try { Save(); set(change.Target); }
                catch { state.Forget(session.Key); Save(); throw; }
            }
            else
            {
                if (!state.Entries.Any(x => x.Key == session.Key)) return;
                var change = state.Restore(session);
                if (change != null) set(change.Target);
                // A changed slider is a user's choice: leave it alone.
                state.Forget(session.Key); Save();
            }
        });
    }
    private void Save()
    {
        if (state.Entries.Count == 0) { if (File.Exists(journal)) File.Delete(journal); return; }
        File.WriteAllText(journal + ".tmp", JsonSerializer.Serialize(state.Entries));
        File.Move(journal + ".tmp", journal, true);
    }
}
