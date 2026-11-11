var root = Path.Combine(Path.GetTempPath(), "wake-steam-" + Guid.NewGuid().ToString("N"));
try {
    var secondary = Path.Combine(root, "other");
    Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "First"));
    Directory.CreateDirectory(Path.Combine(secondary, "steamapps", "common", "Second"));
    var escaped = secondary.Replace("\\", "\\\\");
    File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + escaped + "\" } }");
    void Manifest(string dir, string id, string name, string installed, string flags) => File.WriteAllText(Path.Combine(dir, "steamapps", "appmanifest_" + id + ".acf"),
        $"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"{installed}\" \"StateFlags\" \"{flags}\" }}");
    Manifest(root, "10", "First Game", "First", "4");
    Manifest(secondary, "20", "Second Game", "Second", "4");
    Manifest(root, "30", "Not installed", "Missing", "4");
    Manifest(root, "40", "Downloading", "First", "2");
    Manifest(root, "50", "Traversal", "../../other/steamapps/common/Second", "4");
    var games = SteamCatalog.Scan(root);
    if (games.Count != 2 || !games.Any(x => x.AppId == "20")) throw new Exception("Installed catalog or secondary library broken");
    var parsed = SteamCatalog.Parse("// comment\n\"title\" \"a \\\"quote\\\"\"");
    if ((string)parsed["title"] != "a \"quote\"") throw new Exception("Escaped quote broken");
    Console.WriteLine("Steam catalog: multiple libraries, escapes, incomplete installs and path traversal passed.");
} finally { Directory.Delete(root, true); }

async Task Check(string name, SteamLaunchState[] states, int expectedRetries, SteamLaunchState? initial = null, bool cancel = false)
{
    int step = 0, launches = 0;
    using var cancellation = new CancellationTokenSource();
    if (cancel) cancellation.Cancel();
    try {
        await SteamLaunchFlow.MonitorAsync(() => states[Math.Min(step, states.Length - 1)], () => launches++, _ => {},
            _ => { step++; return Task.CompletedTask; }, 80, cancellation.Token, initial);
        if (cancel) throw new Exception("Cancellation ignored");
    } catch (OperationCanceledException) when (cancel) { }
    if (launches != expectedRetries) throw new Exception(name + ": unexpected launch retry count " + launches);
    Console.WriteLine("Steam launch: " + name + " passed");
}
var ready = new SteamLaunchState(false, true, false);
var updating = new SteamLaunchState(false, false, true);
var running = new SteamLaunchState(true, true, false);
await Check("update completes then retry exactly once", new[] {updating, updating, ready, ready, ready, ready, ready}, 1);
await Check("Steam auto-launch after update never duplicated", new[] {updating, ready, ready, running}, 0);
await Check("slow launcher without update never retried", new[] {ready}, 0);
await Check("paused update never bypassed", new[] {updating}, 0);
await Check("missing manifest never interpreted as completed update", new[] {updating, new SteamLaunchState(false, false, false)}, 0);
await Check("fast update detected by build change", new[] {ready with {BuildId = "2"}}, 1, ready with {BuildId = "1"});
await Check("update observed before initial launch", new[] {ready}, 1, updating);
await Check("cancellation never retries", new[] {updating, ready}, 0, cancel: true);
