using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

internal static class VoiceFrames
{
    internal const int Maximum = 16 * 1024;
    internal static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > Maximum) throw new InvalidDataException("Invalid voice bridge frame");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token); return body;
    }
    internal static async Task WriteAsync(Stream stream, byte[] data, CancellationToken token)
    {
        if (data.Length <= 0 || data.Length > Maximum) throw new InvalidDataException("Invalid voice bridge frame");
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(data, token); await stream.FlushAsync(token);
    }
}

internal sealed class VoiceNativeBridge
{
    internal static string PipeName => "WakeMiniVoice-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..24];
    private readonly object gate = new();
    private NamedPipeServerStream? connection;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Action, TaskCompletionSource<string> Completion)> pending = new();
    private string? voiceSessionId;
    internal bool HasVoiceSession => Volatile.Read(ref voiceSessionId) != null;
    internal event Action? ConversationEnded;
    internal bool Connected { get { lock (gate) return connection?.IsConnected == true; } }

    internal async Task RunServerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(token);
                lock (gate) connection = pipe;
                while (await VoiceFrames.ReadAsync(pipe, token) is byte[] bytes)
                {
                    using var json = JsonDocument.Parse(bytes);
                    var root = json.RootElement;
                    if (root.TryGetProperty("event", out var eventName) && eventName.GetString() == "voice_ended"
                        && root.TryGetProperty("sessionId", out var endedId) && endedId.ValueKind == JsonValueKind.String)
                    {
                        var ended = endedId.GetString();
                        var currentSession = Volatile.Read(ref voiceSessionId);
                        if (ended != null && currentSession == ended && Interlocked.CompareExchange(ref voiceSessionId, null, currentSession) == currentSession)
                            ConversationEnded?.Invoke();
                        continue;
                    }
                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!pending.TryGetValue(id.GetString()!, out var completion)) continue;
                    var ok = root.TryGetProperty("ok", out var value) && value.ValueKind == JsonValueKind.True;
                    var code = root.TryGetProperty("code", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : "";
                    if (ok && code == (completion.Action == "start" ? "VOICE_UI_ACTIVE" : "VOICE_UI_STOPPED")) completion.Completion.TrySetResult(code!);
                    else completion.Completion.TrySetException(new InvalidOperationException(Explain(code)));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException) { }
            finally
            {
                lock (gate) connection = null;
                foreach (var item in pending.Values) item.Completion.TrySetException(new IOException("Связь с расширением Chrome прервалась."));
            }
        }
    }
    private static string Explain(string? code) => code switch {
        "VOICE_CONTROL_NOT_FOUND" => "Не найдена кнопка голосового разговора ChatGPT. Проверьте вход в аккаунт и язык интерфейса; диктовка не нажималась.",
        "VOICE_NOT_CONFIRMED" => "Кнопка нажата один раз, но голосовой интерфейс не подтвердил запуск. Проверьте разрешение микрофона и лимит голоса ChatGPT вручную.",
        "MULTIPLE_CHATGPT_TABS" => "Открыто несколько вкладок ChatGPT. Оставьте одну нужную вкладку перед первым запуском.",
        "AMBIGUOUS_VOICE_CONTROL" => "Найдено несколько кнопок голоса. Автонажатие отменено.",
        "NO_MANAGED_VOICE_TAB" => "Нет вкладки, запущенной этим расширением. Завершите ранее открытый разговор вручную.",
        "VOICE_OPERATION_BUSY" => "Запуск или завершение голоса уже выполняется.",
        "VOICE_CANCELLED" => "Запуск голоса отменён.",
        _ => "Расширение не смогло выполнить голосовую команду. Проверьте страницу ChatGPT вручную."
    };
    internal async Task<string> SendAsync(string action, CancellationToken token)
    {
        if (action is not ("start" or "stop")) throw new ArgumentException("Unsupported voice command");
        NamedPipeServerStream pipe;
        lock (gate) pipe = connection?.IsConnected == true ? connection : throw new InvalidOperationException(
            "Нет связи с расширением. Откройте Chrome с установленным Wake Mini Voice и оставьте его свёрнутым; окно игры помощник не переключает.");
        var id = Guid.NewGuid().ToString("N");
        if (action == "start") Volatile.Write(ref voiceSessionId, id);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, (action, completion))) throw new InvalidOperationException("Voice request collision");
        try
        {
            await writes.WaitAsync(token);
            try { await VoiceFrames.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { id, action }), token); }
            finally { writes.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(42), token);
        }
        catch (OperationCanceledException) { await CancelRequestAsync(pipe, id); throw; }
        catch (TimeoutException) { await CancelRequestAsync(pipe, id); throw new IOException("Расширение Chrome не подтвердило голосовую команду. Проверьте страницу вручную; повтор автоматически не отправлялся."); }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task CancelRequestAsync(Stream pipe, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await writes.WaitAsync(deadline.Token);
            try { await VoiceFrames.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { id, action = "cancel" }), deadline.Token); }
            finally { writes.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    // Chrome launches a separate copy of the EXE with the caller origin as argv[0].
    // This process is a bounded JSON relay only: no UI, logging to stdout, or commands.
    internal static int RunHost(string origin)
    {
        if (origin != "chrome-extension://" + VoiceExtensionIdentity.Id + "/") return 2;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(4000);
            using var stop = new CancellationTokenSource();
            var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
            async Task Copy(Stream source, Stream target)
            {
                while (await VoiceFrames.ReadAsync(source, stop.Token) is byte[] bytes)
                    await VoiceFrames.WriteAsync(target, bytes, stop.Token);
            }
            var a = Copy(input, pipe); var b = Copy(pipe, output);
            Task.WhenAny(a, b).GetAwaiter().GetResult(); stop.Cancel();
            return 0;
        }
        catch { return 3; }
    }
    internal static void RegisterHost(string directory)
    {
        var file = Path.Combine(directory, "wakemini-voice-host.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new {
            name = VoiceExtensionIdentity.Host, description = "Wake Mini ChatGPT voice bridge", path = Environment.ProcessPath,
            type = "stdio", allowed_origins = new[] { "chrome-extension://" + VoiceExtensionIdentity.Id + "/" }
        }));
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Google\Chrome\NativeMessagingHosts\" + VoiceExtensionIdentity.Host);
        key.SetValue("", file);
    }
}
