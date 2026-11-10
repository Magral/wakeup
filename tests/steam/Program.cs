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
