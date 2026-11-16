using System.Text.Json;
static void Equal(float actual, float expected, string message) { if (Math.Abs(actual - expected) > .0001f) throw new Exception(message + $": {actual} != {expected}"); }
var state = new GameAudioDuckingState();
var game = new GameAudioSession("hdmi/game", "game:1", .8f, "hdmi/app", "hdmi");
var change = state.Duck(game) ?? throw new Exception("Game at 80 percent was not attenuated");
Equal(change.Target, .4f, "80 percent becomes 40 percent");
if (state.Duck(game with { Volume = .4f }) != null) throw new Exception("Repeated call ducks twice");
var headset = new GameAudioSession("headset/game", "game:1", .4f, "headset/app", "headset");
Equal(state.Duck(headset, new HashSet<string>{headset.Key})!.Target, .4f, "Migrated attenuated volume must not halve again");
var other = new GameAudioSession("headset/other", "other:2", .6f);
Equal(state.Duck(other)!.Target, .3f, "A newly launched game is ducked");
Equal(state.Restore(game with {Volume = .4f})!.Target, .8f, "Original HDMI level restored");
Equal(state.Restore(headset)!.Target, .8f, "Original level restored on headset");
if (state.Restore(other with {Volume = .2f}) != null) throw new Exception("Manual mixer adjustment was overwritten");
// Persist before touching Windows: a crashed helper must be able to undo attenuation.
var saved = JsonSerializer.Serialize(state.Entries);
var recovered = new GameAudioDuckingState(JsonSerializer.Deserialize<List<GameAudioVolume>>(saved)!);
Equal(recovered.Restore(headset)!.Target, .8f, "Crash recovery retains baseline");
var independent = new GameAudioDuckingState();
independent.Duck(game);
Equal(independent.Duck(headset with {Volume = .6f})!.Target, .3f, "Different endpoint's own mixer level respected");
if (!GameAudioDuckingState.IsGamePath("/games/title/bin/game.exe", new[]{"/games/title"})) throw new Exception("Nested game executable not recognized");
if (GameAudioDuckingState.IsGamePath("/games/title-launcher/app.exe", new[]{"/games/title"})) throw new Exception("Directory prefix is not membership");
if (GameAudioDuckingState.IsGamePath("/chrome/chrome.exe", new[]{"/games/title"})) throw new Exception("Browser would be ducked");
Console.WriteLine("Audio ducking tests passed: relative levels, repeat, migration, new game, restoration, manual changes, recovery, game boundaries.");
var directory = Path.Combine(Path.GetTempPath(), "wakemini-audio-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    var sessions = new List<(GameAudioSession Session, string Path)> {
        (new("hdmi/game", "game:1", .8f), "/games/title/bin/game.exe"),
        (new("chrome", "chrome:2", .9f), "/chrome/chrome.exe")
    };
    void Visit(Action<GameAudioSession, string, Action<float>> callback)
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            int index = i;
            callback(sessions[i].Session, sessions[i].Path, value =>
            {
                if (!File.Exists(Path.Combine(directory, "wake-audio-ducking.json")))
                    throw new Exception("Volume changed without crash recovery journal");
                sessions[index] = (sessions[index].Session with { Volume = value }, sessions[index].Path);
            });
        }
    }
    var service = new GameAudioDucking(directory, _ => { }, Visit);
    if (!service.Begin(new[]{"/games/title"})) throw new Exception("First conversation did not acquire ducking");
    Equal(sessions[0].Session.Volume, .4f, "Service attenuates actual game slider");
    Equal(sessions[1].Session.Volume, .9f, "Service leaves browser voice unchanged");
    if (service.Begin(new[]{"/games/title"})) throw new Exception("Repeated command acquired second ducking owner");
    service.End();
    Equal(sessions[0].Session.Volume, .8f, "Conversation end restores game");
    if (File.Exists(Path.Combine(directory, "wake-audio-ducking.json"))) throw new Exception("Restored journal not cleaned");
    service.Begin(new[]{"/games/title"});
    var restarted = new GameAudioDucking(directory, _ => { }, Visit);
    restarted.End();
    Equal(sessions[0].Session.Volume, .8f, "New helper restores interrupted conversation");
    service.End();
    service.Begin(new[]{"/games/title"});
    sessions.Add((new("headset/newgame", "game:3", 1f), "/games/title/bin/other.exe"));
    using (var timer = new CancellationTokenSource())
    {
        var running = service.RunAsync(timer.Token);
        Equal(sessions[2].Session.Volume, .5f, "Running monitor catches newly started game");
        timer.Cancel(); await running;
    }
    Equal(sessions[2].Session.Volume, 1f, "Helper shutdown restores new game");
    Console.WriteLine("Audio lifecycle passed: browser isolation, disk-before-volume, idempotent start, automatic end, restart recovery, newly started game, shutdown.");
}
finally { Directory.Delete(directory, true); }
var simultaneous = new GameAudioDuckingState();
simultaneous.Duck(game);
var secondStream = game with { Key = "hdmi/game-2", Volume = .4f };
Equal(simultaneous.Duck(secondStream, new HashSet<string>{game.Key, secondStream.Key})!.Target, .2f, "Same-process streams on same endpoint have independent volume");
Equal(simultaneous.Restore(secondStream with {Volume = .2f})!.Target, .4f, "Second stream restores own baseline");
var manualMove = new GameAudioDuckingState();
manualMove.Duck(game);
manualMove.Observe(game with {Volume = .2f});
Equal(manualMove.Duck(headset with {Volume = .2f}, new HashSet<string>{headset.Key})!.Target, .2f, "Transferred manual slider is not halved again");
var restartState = new GameAudioDuckingState(simultaneous.Entries);
var replacement = game with {Key = "hdmi/restarted", Volume = .4f};
if (restartState.Restore(replacement, new HashSet<string>{game.Key, replacement.Key}) != null)
    throw new Exception("New instance consumed still-live old instance's recovery entry");
Equal(restartState.Restore(replacement, new HashSet<string>{replacement.Key})!.Target, .8f, "Recovery uses application identity only after original instance disappeared");
Console.WriteLine("Independent instances, independent streams, migrated user overrides and bounded recovery identity passed.");

var reopened = new GameAudioDuckingState();
reopened.Duck(game);
var newProcess = game with {Key = "hdmi/new-instance", ProcessKey = "game:999", Volume = .4f};
Equal(reopened.Duck(newProcess, new HashSet<string>{newProcess.Key})!.Target, .4f, "Restarted game is not attenuated twice");
Equal(reopened.Restore(newProcess with {Volume = .4f})!.Target, .8f, "Restarted game retains pre-conversation baseline");
Console.WriteLine("Same-endpoint game restart retains original baseline.");
