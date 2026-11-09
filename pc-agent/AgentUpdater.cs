using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class Program
{
    private static int UpdateBusy;
    private static async Task UpdateAgentAsync(string directory, Action exit)
    {
        if (Interlocked.CompareExchange(ref UpdateBusy, 1, 0) != 0) return;
        string? staging = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WakeMini-Updater/1.6.0");
            using var response = await http.GetAsync("https://api.github.com/repos/Magral/wakeup/releases/latest");
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var current = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
            if (!Version.TryParse(tag.TrimStart('v'), out var version)) throw new IOException("Некорректная версия релиза.");
            if (version <= new Version(current.Major, current.Minor, current.Build))
            { MessageBox.Show($"Установлена актуальная версия {current}.", "Wake Mini — обновление"); return; }
            if (MessageBox.Show($"Доступна версия {version}. Сейчас установлена {current}.\n\nСкачать и установить? Помощник перезапустится. Ключ и настройки сохранятся. Предыдущую версию можно вернуть из меню.\n\nОписание: https://github.com/Magral/wakeup/releases/tag/" + tag,
                "Wake Mini — обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            using var progress = new Form { Text = "Wake Mini — обновление", Width = 440, Height = 125,
                StartPosition = FormStartPosition.CenterScreen, ControlBox = false, FormBorderStyle = FormBorderStyle.FixedDialog };
            progress.Controls.Add(new Label { Text = "Скачиваю и проверяю обновление…", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
            progress.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Bottom });
            progress.Show();
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var asset = assets.Single(x => x.GetProperty("name").GetString() == $"WakeMini-{version}.zip");
            var sums = assets.Single(x => x.GetProperty("name").GetString() == "SHA256SUMS.txt");
            static string AssetUrl(JsonElement asset)
            {
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith("https://github.com/Magral/wakeup/releases/download/", StringComparison.Ordinal))
                    throw new IOException("Неожиданный адрес файла релиза.");
                return url;
            }
            var checksum = await http.GetStringAsync(AssetUrl(sums));
            var matching = checksum.Split('\n').Select(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Single(x => x.Length == 2 && x[1] == $"WakeMini-{version}.zip");
            if (matching[0].Length != 64 || !matching[0].All(Uri.IsHexDigit)) throw new IOException("Некорректная контрольная сумма.");
            staging = Path.Combine(directory, ".wake-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var zipPath = Path.Combine(staging, "release.zip");
            using (var download = await http.GetAsync(AssetUrl(asset), HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();
                await using var input = await download.Content.ReadAsStreamAsync();
                await using var output = File.Create(zipPath);
                var buffer = new byte[81920]; long size = 0; int count;
                while ((count = await input.ReadAsync(buffer)) > 0)
                {
                    size += count;
                    if (size > 200 * 1024 * 1024) throw new IOException("Архив обновления слишком большой.");
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
            }
            await using (var stream = File.OpenRead(zipPath))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(matching[0], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма не совпала. Обновление отменено.");
            var stagedExe = Path.Combine(staging, "WakeMiniAgent.exe");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var exe = zip.Entries.Single(x => x.FullName == "WakeMiniAgent.exe");
                if (exe.Length > 250 * 1024 * 1024) throw new IOException("Некорректный размер помощника.");
                exe.ExtractToFile(stagedExe);
            }
            var fileVersion = FileVersionInfo.GetVersionInfo(stagedExe);
            if (fileVersion.FileMajorPart != version.Major || fileVersion.FileMinorPart != version.Minor || fileVersion.FileBuildPart != version.Build)
                throw new IOException("Версия EXE не соответствует релизу.");
            StartUpdateInstaller(directory, stagedExe, staging, false);
            staging = null; exit();
        }
        catch (Exception ex)
        {
            Log(directory, "Обновление не установлено: " + ex.Message);
            MessageBox.Show("Обновление не установлено. Текущая версия продолжает работать.\n\n" + ex.Message,
                "Wake Mini — обновление", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (staging != null) { try { Directory.Delete(staging, true); } catch { } }
            Interlocked.Exchange(ref UpdateBusy, 0);
        }
    }

    private static void RollbackAgent(string directory, Action exit)
    {
        if (Volatile.Read(ref UpdateBusy) != 0) return;
        var backup = Path.Combine(directory, "WakeMiniAgent.exe.previous");
        if (!File.Exists(backup)) { MessageBox.Show("Предыдущей версии пока нет.", "Wake Mini"); return; }
        if (MessageBox.Show("Вернуть предыдущую версию помощника? Настройки сохранятся.", "Wake Mini — откат",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var staging = Path.Combine(directory, ".wake-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var source = Path.Combine(staging, "WakeMiniAgent.exe");
            File.Copy(backup, source);
            StartUpdateInstaller(directory, source, staging, true); exit();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Wake Mini — откат"); }
    }

    private static void StartUpdateInstaller(string directory, string source, string staging, bool rollback)
    {
        // Fixed script, paths passed only as environment values: no interpolation into executable code.
        const string script = """
$ErrorActionPreference = 'Stop'
$target = $env:WAKE_UPDATE_TARGET
$source = $env:WAKE_UPDATE_SOURCE
$backup = $target + '.previous'
$installed = $false
try {
    Wait-Process -Id ([int]$env:WAKE_UPDATE_PID) -Timeout 45 -ErrorAction SilentlyContinue
    if (Get-Process -Id ([int]$env:WAKE_UPDATE_PID) -ErrorAction SilentlyContinue) { throw 'Помощник не завершился.' }
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            if ($env:WAKE_UPDATE_ROLLBACK -eq '1') { [IO.File]::Replace($source, $target, $null) }
            else { [IO.File]::Replace($source, $target, $backup) }
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (!$ready) { throw 'EXE занят или нет права записи. Закройте Chrome и повторите обновление.' }
    $installed = $true
    $child = Start-Process -FilePath $target -WorkingDirectory (Split-Path $target) -PassThru
    Start-Sleep -Seconds 5
    if ($child.HasExited) { throw 'Новая версия завершилась после запуска.' }
    Add-Content -LiteralPath (Join-Path (Split-Path $target) 'wake-update.log') -Value "$(Get-Date -Format s) Обновление установлено."
} catch {
    $problem = $_.Exception.Message
    if ($installed -and $env:WAKE_UPDATE_ROLLBACK -ne '1' -and (Test-Path -LiteralPath $backup)) { [IO.File]::Copy($backup, $target, $true) }
    Start-Process -FilePath $target -WorkingDirectory (Split-Path $target)
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show("Обновление не завершено. Запущена сохранённая версия. " + $problem, 'Wake Mini') | Out-Null
} finally {
    Remove-Item -LiteralPath $env:WAKE_UPDATE_STAGING -Recurse -Force -ErrorAction SilentlyContinue
}
""";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))
        };
        info.Environment["WAKE_UPDATE_TARGET"] = Environment.ProcessPath!;
        info.Environment["WAKE_UPDATE_SOURCE"] = source;
        info.Environment["WAKE_UPDATE_STAGING"] = staging;
        info.Environment["WAKE_UPDATE_PID"] = Environment.ProcessId.ToString();
        info.Environment["WAKE_UPDATE_ROLLBACK"] = rollback ? "1" : "0";
        _ = Process.Start(info) ?? throw new IOException("Не удалось запустить установку обновления.");
    }
}
