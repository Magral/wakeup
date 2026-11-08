using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class LgTvSettings
{
    public string Address { get; set; } = "";
    public string Mac { get; set; } = "";
    public int Hdmi { get; set; } = 1;
    public bool OnStartup { get; set; }
    public bool Secure { get; set; } = true;
    public string? ClientKey { get; set; }
    public string? CertificateHash { get; set; }

    internal IPAddress Validate()
    {
        if (!IPAddress.TryParse(Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Укажите локальный IPv4-адрес телевизора.");
        var b = address.GetAddressBytes();
        if (!(b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31))
            throw new ArgumentException("Телевизор должен иметь локальный адрес: 10.x.x.x, 172.16–31.x.x или 192.168.x.x.");
        _ = MagicPacket(Mac);
        if (Hdmi < 1 || Hdmi > 4) throw new ArgumentException("Выберите HDMI от 1 до 4.");
        return address;
    }

    internal static byte[] MagicPacket(string value)
    {
        var normalized = value.Replace(":", "").Replace("-", "").Replace(" ", "");
        if (normalized.Length != 12 || !normalized.All(Uri.IsHexDigit))
            throw new ArgumentException("Укажите MAC сетевого подключения телевизора: 12 hex-символов.");
        var mac = Convert.FromHexString(normalized);
        if (mac.All(x => x == 0) || (mac[0] & 1) != 0) throw new ArgumentException("Неверный MAC телевизора.");
        var packet = new byte[102];
        Array.Fill(packet, (byte)255, 0, 6);
        for (var i = 0; i < 16; i++) mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }
}

// Local SSAP only. Pair explicitly on the TV; subsequent TLS sessions pin its certificate.
// No power-off, idle polling, display settings, or incoming TCP listener.
internal sealed class LgTvProtocolException : InvalidOperationException
{
    internal int Code { get; }
    internal string Stage { get; }
    internal LgTvProtocolException(int code, string stage) : base(
        $"LG: {stage}: код {code}. " + (code is 401 or 403
            ? "Нет разрешения или отклонено сопряжение. Выполните повторное сопряжение и подтвердите запрос на ТВ."
            : code == 404 ? "Телевизор не поддерживает этот метод управления."
            : "Телевизор отклонил запрос.")) { Code = code; Stage = stage; }
}

internal sealed class LgWebOsClient
{
    private readonly LgTvSettings settings;
    private readonly Action<LgTvSettings> save;
    private readonly Func<ClientWebSocket, CancellationToken, Task>? connect;
    private readonly Func<IPAddress, CancellationToken, Task>? wake;
    internal LgWebOsClient(LgTvSettings settings, Action<LgTvSettings> save,
        Func<ClientWebSocket, CancellationToken, Task>? connect = null,
        Func<IPAddress, CancellationToken, Task>? wake = null)
    { this.settings = settings; this.save = save; this.connect = connect; this.wake = wake; }

    internal async Task PairAsync(CancellationToken token)
    {
        settings.Validate();
        using var socket = NewSocket(true, out var certificate);
        await ConnectAsync(socket, token);
        var key = await RegisterAsync(socket, null, token);
        // Persist credentials only after the TV has explicitly approved registration.
        settings.ClientKey = key;
        settings.CertificateHash = certificate();
        save(settings);
    }

    internal async Task ShowPcAsync(CancellationToken token)
    {
        var address = settings.Validate();
        if (string.IsNullOrWhiteSpace(settings.ClientKey) || settings.Secure && string.IsNullOrWhiteSpace(settings.CertificateHash))
            throw new InvalidOperationException("Сначала выполните сопряжение с телевизором в настройках Wake Mini.");
        Exception? last = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var socket = NewSocket(false, out var certificate);
            try
            {
                await ConnectAsync(socket, token);
                await RegisterAsync(socket, settings.ClientKey, token);
                await SelectHdmiAsync(socket, settings.Hdmi, token);
                return;
            }
            catch (Exception ex) when ((ex is WebSocketException || ex is IOException || ex is OperationCanceledException)
                && !token.IsCancellationRequested)
            {
                if (settings.Secure && certificate() is string observed
                    && !string.Equals(observed, settings.CertificateHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Сертификат LG изменился. Проверьте IP телевизора и выполните повторное сопряжение.");
                // A waking TV can accept TCP/TLS and then close the SSAP socket.
                // Retry transport startup only, never rejection of credentials/permissions.
                last = ex;
                await WakeAsync(address, token);
                await Task.Delay(2000, token);
            }
        }
        throw new IOException("Телевизор не готов к управлению: соединение не установлено или закрывается при загрузке. Проверьте питание, IP/MAC и «Включение ТВ с мобильного».", last);
    }

    private static async Task SelectHdmiAsync(ClientWebSocket socket, int hdmi, CancellationToken token)
    {
        try
        {
            var payload = await RequestAsync(socket, "tv/switchInput", new { inputId = $"HDMI_{hdmi}" }, token);
            Confirm(payload);
        }
        catch (LgTvProtocolException ex) when (ex.Code is 401 or 403 or 404)
        {
            // Both APIs select the same configured HDMI. Never use caller-supplied app IDs.
            var payload = await RequestAsync(socket, "system.launcher/launch", new { id = $"com.webos.app.hdmi{hdmi}" }, token);
            Confirm(payload);
        }
        static void Confirm(JsonElement payload)
        {
            if (!payload.TryGetProperty("returnValue", out var result) || result.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Телевизор не подтвердил переключение HDMI.");
        }
    }

    private ClientWebSocket NewSocket(bool pairing, out Func<string?> certificate)
    {
        string? observed = null;
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
        {
            if (cert == null) return false;
            observed = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
            return pairing || string.Equals(observed, settings.CertificateHash, StringComparison.Ordinal);
        };
        certificate = () => observed;
        return socket;
    }

    private async Task ConnectAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        if (connect != null) { await connect(socket, timeout.Token); return; }
        await socket.ConnectAsync(new Uri($"{(settings.Secure ? "wss" : "ws")}://{settings.Address}:{(settings.Secure ? 3001 : 3000)}"), timeout.Token);
    }

    private static async Task<string> RegisterAsync(ClientWebSocket socket, string? key, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["pairingType"] = "PROMPT", ["forcePairing"] = key == null,
            ["manifest"] = new { manifestVersion = 1, appVersion = "1.4.1",
                permissions = new[] { "CONTROL_INPUT_TV", "READ_INPUT_DEVICE_LIST", "LAUNCH" } }
        };
        if (key != null) payload["client-key"] = key;
        await SendAsync(socket, new { id = "register", type = "register", payload }, token);
        while (true)
        {
            var message = await ReceiveAsync(socket, token);
            ThrowIfError(message, "сопряжение / регистрация");
            if (message.TryGetProperty("type", out var type) && type.GetString() == "registered"
                && message.TryGetProperty("payload", out var body) && body.TryGetProperty("client-key", out var clientKey)
                && !string.IsNullOrWhiteSpace(clientKey.GetString())) return clientKey.GetString()!;
            // A saved key must never trigger an unattended pairing prompt.
            if (key != null && message.TryGetProperty("payload", out var p) && p.TryGetProperty("pairingType", out _))
                throw new InvalidOperationException("Телевизор требует повторного сопряжения. Выполните его в настройках Wake Mini.");
        }
    }

    internal static async Task<JsonElement> RequestAsync(ClientWebSocket socket, string uri, object payload, CancellationToken token)
    {
        var id = "request-" + Guid.NewGuid().ToString("N");
        await SendAsync(socket, new { id, type = "request", uri = "ssap://" + uri, payload }, token);
        while (true)
        {
            var message = await ReceiveAsync(socket, token);
            if (!message.TryGetProperty("id", out var responseId) || responseId.GetString() != id) continue;
            ThrowIfError(message, uri);
            var payloadBody = message.GetProperty("payload");
            if (payloadBody.TryGetProperty("returnValue", out var value) && value.ValueKind == JsonValueKind.False
                && payloadBody.TryGetProperty("errorCode", out var errorCode) && errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out var code))
                throw new LgTvProtocolException(code, uri);
            return payloadBody.Clone();
        }
    }

    private static void ThrowIfError(JsonElement message, string stage)
    {
        if (!message.TryGetProperty("type", out var type) || type.GetString() != "error") return;
        var code = 0;
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var prefix = (error.GetString() ?? "").Split(' ', 2)[0];
            int.TryParse(prefix, out code);
        }
        // Only a code and our local phase are surfaced. Do not echo arbitrary TV JSON or client-key.
        throw new LgTvProtocolException(code, stage);
    }

    private static async Task SendAsync(ClientWebSocket socket, object message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException($"Телевизор закрыл соединение (WebSocket: {result.CloseStatus}).");
            body.Write(buffer, 0, result.Count);
            if (body.Length > 65536) throw new IOException("Слишком большой ответ телевизора.");
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(body.ToArray());
        return document.RootElement.Clone();
    }

    private async Task WakeAsync(IPAddress address, CancellationToken token)
    {
        if (wake != null) { await wake(address, token); return; }
        var targets = new HashSet<IPAddress> { address, IPAddress.Broadcast };
        var target = address.GetAddressBytes();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var unicast in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var local = unicast.Address.GetAddressBytes();
            var mask = unicast.IPv4Mask.GetAddressBytes();
            if (Enumerable.Range(0, 4).All(i => (local[i] & mask[i]) == (target[i] & mask[i])))
                targets.Add(new IPAddress(Enumerable.Range(0, 4).Select(i => (byte)(target[i] | ~mask[i])).ToArray()));
        }
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var packet = LgTvSettings.MagicPacket(settings.Mac);
        foreach (var targetAddress in targets)
        {
            try { await udp.SendAsync(packet.AsMemory(), new IPEndPoint(targetAddress, 9), token); }
            catch (SocketException) { /* Another local adapter/broadcast may still deliver the packet. */ }
        }
    }
}
