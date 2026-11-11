using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private sealed class SteamSettings
    {
        public bool Enabled { get; set; }
        public string SteamPath { get; set; } = "";
        public string CloudUrl { get; set; } = "";
    }
    private static string SteamLaunchMessage = "";
    private static string SteamMessage = "Steam: облачная связь не настроена.";
    private static string SteamPath(string directory) => Path.Combine(directory, "wake-steam.json");
    private static SteamSettings ReadSteamSettings(string directory) => File.Exists(SteamPath(directory))
        ? JsonSerializer.Deserialize<SteamSettings>(File.ReadAllText(SteamPath(directory))) ?? new() : new();
    private static void ConfigureSteam(string directory)
    {
        var settings = ReadSteamSettings(directory);
        var pair = SettingsWindow("Steam: библиотека и облако"); using var form = pair.Form; var grid = pair.Grid;
        var enabled = Row(grid, new CheckBox {Text = "Разрешить запуск установленных игр из навыка Стим", Checked = settings.Enabled, AutoSize = true});
        var path = Field(grid, "Путь к steam.exe", string.IsNullOrEmpty(settings.SteamPath) ? DetectSteam() : settings.SteamPath);
        var url = Field(grid, "HTTPS-адрес функции Яндекс Облака (без параметров)", settings.CloudUrl);
        var save = Row(grid, new Button {Text = "Сохранить", AutoSize = true});
        var scan = Row(grid, new Button {Text = "Обновить список установленных игр", AutoSize = true});
        var status = Row(grid, new Label {AutoSize = true, MaximumSize = new(700, 0), Text = SteamMessage});
        var list = Row(grid, new ListBox {Height = 240});
        scan.Click += (_, _) => {
            try { list.Items.Clear(); foreach (var game in SteamCatalog.Scan(Path.GetDirectoryName(path.Text)!)) list.Items.Add(game.Name + " — " + game.AppId); status.Text = "Найдено игр: " + list.Items.Count; }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        save.Click += (_, _) => {
            try {
                if (!Path.IsPathFullyQualified(path.Text) || !File.Exists(path.Text) || !Path.GetFileName(path.Text).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Выберите установленный steam.exe.");
                ValidateSteamUrl(url.Text);
                File.WriteAllText(SteamPath(directory) + ".tmp", JsonSerializer.Serialize(new SteamSettings {Enabled = enabled.Checked, SteamPath = path.Text, CloudUrl = url.Text}, new JsonSerializerOptions {WriteIndented = true}));
                File.Move(SteamPath(directory) + ".tmp", SteamPath(directory), true);
                status.Text = "Сохранено. Синхронизация начнётся в течение 5 секунд.";
            } catch (Exception ex) { status.Text = ex.Message; }
        };
        var timer = new System.Windows.Forms.Timer {Interval = 3000}; timer.Tick += (_, _) => status.Text = SteamMessage;
        form.FormClosed += (_, _) => timer.Dispose(); timer.Start(); form.ShowDialog();
    }
    private static Uri ValidateSteamUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "functions.yandexcloud.net"
            || uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new IOException("Нужен HTTPS-адрес functions.yandexcloud.net без параметров.");
        return uri;
    }
    private static async Task<JsonDocument> SteamRequest(HttpClient http, SteamSettings settings, byte[] key, string op, object payload, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new {op, payload});
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var prefix = Encoding.UTF8.GetBytes(timestamp + "\n");
        var signed = prefix.Concat(body).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, ValidateSteamUrl(settings.CloudUrl));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Wake-Steam-Time", timestamp);
        request.Headers.Add("X-Wake-Steam-Sign", Convert.ToHexString(HMACSHA256.HashData(key, signed)));
        using var response = await http.SendAsync(request, token); response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsByteArrayAsync(token);
        if (result.Length > 65536) throw new IOException("Слишком большой ответ очереди.");
        var expected = HMACSHA256.HashData(key, prefix.Concat(result).ToArray());
        if (!response.Headers.TryGetValues("X-Wake-Steam-Sign", out var signatures)
            || !CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signatures.Single()))) throw new IOException("Подпись ответа очереди не совпала.");
        return JsonDocument.Parse(result);
    }
    private static async Task RunSteamCloudAsync(string directory, byte[] key, CancellationToken token)
    {
        using var http = new HttpClient {Timeout = TimeSpan.FromSeconds(15)};
        IReadOnlyList<SteamGame> games = Array.Empty<SteamGame>();
        string lastCatalog = ""; var lastSync = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var settings = ReadSteamSettings(directory);
                if (settings.Enabled)
                {
                    if (DateTime.UtcNow - lastSync > TimeSpan.FromSeconds(60))
                    {
                        games = SteamCatalog.Scan(Path.GetDirectoryName(settings.SteamPath)!);
                        var catalog = JsonSerializer.Serialize(games.Select(x => new {appid = x.AppId, name = x.Name}));
                        var fingerprint = settings.CloudUrl + catalog;
                        if (fingerprint != lastCatalog || DateTime.UtcNow - lastSync > TimeSpan.FromHours(1))
                        {
                            using var uploaded = await SteamRequest(http, settings, key, "catalog", new {games = games.Select(x => new {appid = x.AppId, name = x.Name})}, token);
                            lastCatalog = fingerprint;
                        }
                        lastSync = DateTime.UtcNow;
                    }
                    using var next = await SteamRequest(http, settings, key, "next", new {}, token);
                    if (next.RootElement.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object)
                        await ExecuteSteamJob(directory, settings, key, http, games, job, token);
                    else SteamMessage = $"Steam: связь с облаком есть; установлено игр: {games.Count}. " + SteamLaunchMessage;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { SteamMessage = "Steam: " + ex.Message; }
            try { await Task.Delay(5000, token); } catch (OperationCanceledException) { return; }
        }
    }
    private static bool IsSteamGameRunning(SteamGame game)
    {
        foreach (var process in Process.GetProcesses())
            using (process) { try {
                var exe = process.MainModule?.FileName;
                if (exe != null && exe.StartsWith(game.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {} }
        return false;
    }
    private static void FocusSteamGame(SteamGame game)
    {
        foreach (var process in Process.GetProcesses())
            using (process) { try {
                var exe = process.MainModule?.FileName;
                if (exe != null && exe.StartsWith(game.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                    BringProgramForward(process.ProcessName); return;
                }
            } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {} }
    }
    private static SteamLaunchState InspectSteamLaunch(SteamGame game)
    {
        if (IsSteamGameRunning(game)) return new(true, false, false);
        try
        {
            if (SteamCatalog.Parse(File.ReadAllText(game.ManifestPath)).GetValueOrDefault("AppState") is not Dictionary<string, object> state)
                return new(false, false, false);
            if (!uint.TryParse(state.GetValueOrDefault("StateFlags") as string, out var flags)) return new(false, false, false);
            // Read-only interpretation of Steam's appmanifest state flags.
            // Never edit manifests, disable updates or bypass Steam prompts.
            const uint updates = 2 | 256 | 512 | 1024 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608;
            var updating = (flags & updates) != 0;
            return new((flags & 8192) != 0, (flags & 4) != 0 && !updating, updating, state.GetValueOrDefault("buildid") as string ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return new(false, false, false); }
    }
    private static async Task ExecuteSteamJob(string directory, SteamSettings settings, byte[] key, HttpClient http, IReadOnlyList<SteamGame> games, JsonElement job, CancellationToken token)
    {
        var id = job.GetProperty("id").GetString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$") || job.GetProperty("expires").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;
        var appid = job.GetProperty("appid").GetString();
        var game = games.SingleOrDefault(x => x.AppId == appid);
        if (game == null) {
            using var missing = await SteamRequest(http, settings, key, "result", new {id, result = "failed"}, token);
            SteamMessage = "Игра больше не установлена: " + appid; Log(directory, SteamMessage); return;
        }
        var ledgerPath = Path.Combine(directory, "wake-steam-jobs.json");
        var ledger = File.Exists(ledgerPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ledgerPath)) ?? new() : new Dictionary<string, string>();
        if (!ledger.TryGetValue(id, out var result))
        {
            lock (TvLock) {
                if (ChatOperation != null || TvOperation != null || Interlocked.CompareExchange(ref DesktopBusy, 1, 0) != 0) return;
            }
            void Save() {
                while (ledger.Count > 256) ledger.Remove(ledger.Keys.First());
                File.WriteAllText(ledgerPath + ".tmp", JsonSerializer.Serialize(ledger)); File.Move(ledgerPath + ".tmp", ledgerPath, true);
            }
            var ownsDesktop = true;
            try
            {
                // Persist before side effects. An interrupted command is reported, never replayed.
                ledger[id] = "interrupted"; Save();
                var tv = ReadTvSettings(directory); tv.Hdmi = 1; tv.Validate();
                await new LgWebOsClient(tv, _ => {}).ShowPcAsync(token);
                // Explicit Big Picture request also switches an already-running
                // desktop Steam client; a startup preference alone does not.
                if (!IsProgramRunning("steam"))
                    Process.Start(new ProcessStartInfo(settings.SteamPath, "-bigpicture") {UseShellExecute = true});
                for (var wait = 0; wait < 60 && !HasProgramWindow("steam") && !HasProgramWindow("steamwebhelper"); wait++)
                    await Task.Delay(500, token);
                Process.Start(new ProcessStartInfo(settings.SteamPath, "steam://open/bigpicture") {UseShellExecute = true});
                SteamLaunchMessage = game.Name + ": открываю Big Picture…"; SteamMessage = SteamLaunchMessage;
                await Task.Delay(2500, token);
                BringProgramForward("steam", "steamwebhelper");
                if (IsSteamGameRunning(game))
                {
                    FocusSteamGame(game);
                    result = "already_running";
                    SteamLaunchMessage = game.Name + ": игра уже работает; повторный запуск не отправлялся.";
                }
                else
                {
                    void Launch() => Process.Start(new ProcessStartInfo(settings.SteamPath, "steam://rungameid/" + game.AppId) {UseShellExecute = true});
                    var initial = InspectSteamLaunch(game);
                    Launch();
                    // Downloading can take minutes. Release desktop switching so
                    // Work/HDMI/Atlas continue working during the observation.
                    Interlocked.Exchange(ref DesktopBusy, 0); ownsDesktop = false;
                    var lastProgress = "";
                    result = await SteamLaunchFlow.MonitorAsync(() => InspectSteamLaunch(game), Launch, message => {
                        SteamLaunchMessage = game.Name + ": " + message; SteamMessage = SteamLaunchMessage;
                        if (message != lastProgress) { Log(directory, "Steam: " + SteamLaunchMessage); lastProgress = message; }
                    }, ct => Task.Delay(2000, ct), 600, token, initial);
                }
            }
            catch (Exception ex) { result = "failed"; Log(directory, "Steam: " + ex.Message); }
            finally { if (ownsDesktop) Interlocked.Exchange(ref DesktopBusy, 0); }
            ledger[id] = result!; Save();
        }
        using var ack = await SteamRequest(http, settings, key, "result", new {id, result}, token);
        SteamMessage = "Steam: " + game.Name + " — " + result + ". " + SteamLaunchMessage; Log(directory, SteamMessage);
    }
}
