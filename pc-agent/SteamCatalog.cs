using System.Text.RegularExpressions;

internal sealed record SteamGame(string AppId, string Name, string Directory);
internal static class SteamCatalog
{
    // Valve KeyValues: quoted strings, escaped quotes/backslashes, braces and comments.
    internal static Dictionary<string, object> Parse(string text)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]")
            .Select(x => x.Value).Where(x => !x.StartsWith("//")).ToArray();
        int i = 0;
        string Unquote(string s) => Regex.Replace(s[1..^1], @"\\([\\" + "\"" + "])", "$1");
        Dictionary<string, object> Read(bool nested)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (i < tokens.Length)
            {
                var key = tokens[i++];
                if (key == "}") { if (!nested) throw new FormatException("Unexpected brace"); return result; }
                if (!key.StartsWith('"') || i >= tokens.Length) throw new FormatException("Invalid KeyValues");
                var value = tokens[i++];
                result[Unquote(key)] = value == "{" ? Read(true) : value.StartsWith('"') ? Unquote(value) : throw new FormatException("Invalid value");
            }
            if (nested) throw new FormatException("Missing brace");
            return result;
        }
        return Read(false);
    }
    internal static IReadOnlyList<SteamGame> Scan(string root)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {root};
        var folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
        {
            var parsed = Parse(File.ReadAllText(folders));
            if (parsed.GetValueOrDefault("libraryfolders") is Dictionary<string, object> values)
                foreach (var item in values.Where(x => uint.TryParse(x.Key, out _)))
                {
                    var path = item.Value is string old ? old : (item.Value as Dictionary<string, object>)?.GetValueOrDefault("path") as string;
                    if (!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path)) libraries.Add(path);
                }
        }
        var result = new Dictionary<string, SteamGame>();
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!System.IO.Directory.Exists(apps)) continue;
            foreach (var file in System.IO.Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(5000))
            {
                try
                {
                    if (Parse(File.ReadAllText(file)).GetValueOrDefault("AppState") is not Dictionary<string, object> state) continue;
                    var id = state.GetValueOrDefault("appid") as string ?? "";
                    var name = state.GetValueOrDefault("name") as string ?? "";
                    var installed = state.GetValueOrDefault("installdir") as string ?? "";
                    if (!uint.TryParse(id, out var number) || number == 0 || name.Length is < 1 or > 256 || installed.Length == 0) continue;
                    if (!uint.TryParse(state.GetValueOrDefault("StateFlags") as string, out var flags) || (flags & 4) == 0) continue;
                    var common = Path.GetFullPath(Path.Combine(apps, "common")) + Path.DirectorySeparatorChar;
                    var path = Path.GetFullPath(Path.Combine(common, installed));
                    if (!path.StartsWith(common, StringComparison.OrdinalIgnoreCase) || !System.IO.Directory.Exists(path)) continue;
                    result[id] = new(id, name, path);
                }
                catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or UnauthorizedAccessException) { }
            }
        }
        return result.Values.OrderBy(x => x.Name).ToArray();
    }
}
