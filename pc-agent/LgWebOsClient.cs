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
internal sealed class LgWebOsClient
{
    private readonly LgTvSettings settings;
    private readonly Action<LgTvSettings> save;
    private readonly Func<ClientWebSocket, CancellationToken, Task>? connect;
    internal LgWebOsClient(LgTvSettings settings, Action<LgTvSettings> save,
        Func<ClientWebSocket, CancellationToken, Task>? connect = null)
    { this.settings = settings; this.save = save; this.connect = connect; }

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
        // Bounded retries let the network and TV start. Authentication/SSAP failures are not retried.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var socket = NewSocket(false, out _);
            try { await ConnectAsync(socket, token); }
            catch (Exception ex) when ((ex is WebSocketException || ex is OperationCanceledException) && !token.IsCancellationRequested)
            {
                last = ex;
                await WakeAsync(address, token);
                await Task.Delay(2000, token);
                continue;
            }
            await RegisterAsync(socket, settings.ClientKey, token);
            var payload = await RequestAsync(socket, "tv/switchInput", new { inputId = $"HDMI_{settings.Hdmi}" }, token);
            if (!payload.TryGetProperty("returnValue", out var result) || result.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Телевизор не подтвердил переключение HDMI.");
            return;
        }
        throw new IOException("Телевизор не ответил. Проверьте питание, IP/MAC и «Включение ТВ с мобильного».", last);
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
            ["pairingType"] = "PROMPT", ["forcePairing"] = false,
            ["manifest"] = new { manifestVersion = 1, appVersion = "1.4.0",
                permissions = new[] { "CONTROL_INPUT_TV", "READ_INPUT_DEVICE_LIST" } }
        };
        if (key != null) payload["client-key"] = key;
        await SendAsync(socket, new { id = "register", type = "register", payload }, token);
        while (true)
        {
            var message = await ReceiveAsync(socket, token);
            ThrowIfError(message);
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
        const string id = "select-input";
        await SendAsync(socket, new { id, type = "request", uri = "ssap://" + uri, payload }, token);
        while (true)
        {
            var message = await ReceiveAsync(socket, token);
            if (!message.TryGetProperty("id", out var responseId) || responseId.GetString() != id) continue;
            ThrowIfError(message);
            return message.GetProperty("payload").Clone();
        }
    }

    private static void ThrowIfError(JsonElement message)
    {
        if (message.TryGetProperty("type", out var type) && type.GetString() == "error")
            throw new InvalidOperationException("Телевизор отклонил команду. Проверьте сопряжение и разрешение управления.");
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
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Телевизор закрыл соединение.");
            body.Write(buffer, 0, result.Count);
            if (body.Length > 65536) throw new IOException("Слишком большой ответ телевизора.");
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(body.ToArray());
        return document.RootElement.Clone();
    }

    private async Task WakeAsync(IPAddress address, CancellationToken token)
    {
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
