using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private const int Port = 3000;
    private sealed record Challenge(long ExpiresAt, IPEndPoint RemoteEndPoint);
    private static readonly ConcurrentDictionary<string, Challenge> Challenges = new();

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--install-firewall") return InstallFirewallRule();

        var keyPath = Path.Combine(AppContext.BaseDirectory, "wake.key");
        var newKey = !File.Exists(keyPath);
        byte[] key;
        if (newKey)
        {
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(keyPath, Convert.ToHexString(key));
            try { File.SetAttributes(keyPath, File.GetAttributes(keyPath) | FileAttributes.Hidden); } catch { }
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Создан ключ помощника. Введите его в Wake Mini на телефоне:");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(Convert.ToHexString(key));
            Console.ResetColor();
            Console.WriteLine("Не отправляйте и не публикуйте этот ключ.");
        }
        else
        {
            try { key = Convert.FromHexString(File.ReadAllText(keyPath).Trim()); }
            catch
            {
                Console.Error.WriteLine("Файл wake.key повреждён. Удалите его и запустите помощник снова, чтобы создать новый ключ.");
                return 2;
            }
            if (key.Length != 32)
            {
                Console.Error.WriteLine("В wake.key должен быть ключ из 64 шестнадцатеричных символов.");
                return 2;
            }
            Console.WriteLine("Используется ключ из wake.key. На телефоне должен быть указан тот же ключ.");
        }

        Console.WriteLine($"Wake Mini Agent слушает UDP-порт {Port}. Оставьте это окно открытым или добавьте программу в автозагрузку.");
        Console.WriteLine("Остановка помощника: Ctrl+C.");

        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            while (true)
            {
                var datagram = await udp.ReceiveAsync();
                _ = HandleDatagramAsync(udp, datagram, key);
            }
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"Не удалось открыть UDP-порт {Port}: {ex.Message}");
            return 3;
        }
    }

    private static async Task HandleDatagramAsync(UdpClient udp, UdpReceiveResult datagram, byte[] key)
    {
        try
        {
            var text = Encoding.UTF8.GetString(datagram.Buffer);
            var fields = text.Split('|');
            if (fields.Length == 3 && fields[0] == "WM1" && fields[1] == "PING" && IsNonce(fields[2]))
            {
                PruneChallenges();
                var expiry = DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds();
                if (!Challenges.TryAdd(fields[2], new Challenge(expiry, datagram.RemoteEndPoint))) return;
                var response = $"WM1|ONLINE|{fields[2]}|{Sign(key, $"ONLINE|{fields[2]}")}";
                await SendAsync(udp, response, datagram.RemoteEndPoint);
                return;
            }

            if (fields.Length != 4 || fields[0] != "WM1" || !IsAllowedCommand(fields[1]) || !IsNonce(fields[2])) return;
            if (!Challenges.TryRemove(fields[2], out var challenge)
                || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > challenge.ExpiresAt
                || !challenge.RemoteEndPoint.Equals(datagram.RemoteEndPoint)) return;
            var command = fields[1];
            if (!Verify(key, $"{command}|{fields[2]}", fields[3])) return;

            var replyVerb = command == "STATUS" ? "STATUS_ONLINE" : command + "_ACCEPTED";
            var ack = $"WM1|{replyVerb}|{fields[2]}|{Sign(key, $"{replyVerb}|{fields[2]}")}";
            await SendAsync(udp, ack, datagram.RemoteEndPoint);
            if (command == "STATUS") return;

            var description = command switch
            {
                "SHUTDOWN" => "выключения",
                "RESTART" => "перезагрузки",
                "SLEEP" => "спящего режима",
                _ => "команды"
            };
            Console.WriteLine($"{DateTime.Now:T}: принята команда {description}.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                if (command == "SLEEP")
                {
                    if (!SetSuspendState(false, false, false))
                        Console.Error.WriteLine($"Не удалось включить спящий режим Windows: код {Marshal.GetLastWin32Error()}.");
                    return;
                }

                var arguments = command == "RESTART" ? "/r /t 10" : "/s /t 10";
                Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка обработки пакета: {ex.Message}");
        }
    }

    private static async Task SendAsync(UdpClient udp, string text, IPEndPoint endpoint)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await udp.SendAsync(bytes, bytes.Length, endpoint);
    }

    private static void PruneChallenges()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var item in Challenges)
            if (item.Value.ExpiresAt < now) Challenges.TryRemove(item.Key, out _);
    }

    private static bool IsNonce(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    private static bool IsAllowedCommand(string command) =>
        command is "SHUTDOWN" or "RESTART" or "SLEEP" or "STATUS";

    private static string Sign(byte[] key, string message)
    {
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static bool Verify(byte[] key, string message, string provided)
    {
        try
        {
            var expected = Convert.FromHexString(Sign(key, message));
            var actual = Convert.FromHexString(provided);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    private static int InstallFirewallRule()
    {
        try
        {
            var command = new ProcessStartInfo("netsh.exe",
                "advfirewall firewall add rule name=\"Wake Mini Agent UDP 3000\" dir=in action=allow protocol=UDP localport=3000 profile=any")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            using var process = Process.Start(command);
            process!.WaitForExit();
            Console.WriteLine(process.ExitCode == 0 ? "Правило брандмауэра добавлено." : "Не удалось добавить правило брандмауэра.");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось настроить Брандмауэр Windows: {ex.Message}");
            return 4;
        }
    }
}
