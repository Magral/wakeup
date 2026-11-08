using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Reject(Func<Task> work, string message)
{
    try { await work(); } catch (InvalidOperationException) { return; }
    throw new Exception(message);
}
foreach (var bad in new[] { "127.0.0.1", "8.8.8.8", "https://192.168.1.10", "::1", "192.168.1.256" })
{
    try { new LgTvSettings { Address = bad, Mac = "02:11:22:33:44:55" }.Validate(); throw new Exception("Invalid address accepted"); }
    catch (ArgumentException) { }
}
var packet = LgTvSettings.MagicPacket("02:11:22:33:44:55");
Assert(packet.Length == 102 && packet.Take(6).All(x => x == 255), "Bad WoL header");
for (int i = 0; i < 16; i++) Assert(packet.Skip(6 + i * 6).Take(6).SequenceEqual(Convert.FromHexString("021122334455")), "Bad WoL MAC");

// Inject only the transport endpoint: protocol, TLS pinning and LAN setting validation stay real.
var address = IPAddress.Loopback;
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=WakeMini mock TV", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = deadline.Token;
var listener = new TcpListener(address, 0);
listener.Start();
try
{
    var settings = new LgTvSettings { Address = "192.168.1.99", Mac = "02:11:22:33:44:55", Hdmi = 3 };
    int saves = 0, wakes = 0;
    var client = new LgWebOsClient(settings, _ => saves++, (ws, ct) => ws.ConnectAsync(new Uri($"wss://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), ct), (_, _) => { wakes++; return Task.CompletedTask; });
    async Task Scenario(Func<WebSocket, Task> handle, Func<Task> work, int connections = 1)
    {
        var serve = Task.Run(async () =>
        {
            for (var connection = 0; connection < connections; connection++)
            {
            using var tcp = await listener.AcceptTcpClientAsync(token);
            using var ssl = new SslStream(tcp.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await ssl.ReadAsync(one, token) == 0) throw new IOException("No handshake");
                bytes.Add(one[0]);
                if (bytes.Count > 16384) throw new Exception("Huge headers");
                if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10})) break;
            }
            string headers = Encoding.ASCII.GetString(bytes.ToArray());
            var key = headers.Split("\r\n").First(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), token);
            using var socket = WebSocket.CreateFromStream(ssl, true, null, Timeout.InfiniteTimeSpan);
            await handle(socket);
            }
        }, token);
        await Task.WhenAll(work(), serve);
    }
    async Task<JsonElement> Read(WebSocket ws)
    {
        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return doc.RootElement.Clone();
    }
    async Task Send(WebSocket ws, object body)
    { await ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(body)), WebSocketMessageType.Text, true, token); }
    async Task Register(WebSocket ws, bool paired)
    {
        var registration = await Read(ws);
        Assert(registration.GetProperty("type").GetString() == "register", "Not a registration");
        var body = registration.GetProperty("payload");
        Assert(body.TryGetProperty("client-key", out _) == paired, "Wrong pairing credentials");
        Assert(body.GetProperty("forcePairing").GetBoolean() == !paired, "Explicit pairing was not forced");
        var manifest = body.GetProperty("manifest");
        Assert(!manifest.TryGetProperty("signed", out _) && !manifest.TryGetProperty("signatures", out _), "Legacy LG test signature must not be sent");
        var permissions = manifest.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert(permissions.Contains("LAUNCH") && permissions.Contains("CONTROL_INPUT_TV"), "Required HDMI permissions missing");
        if (paired) Assert(body.GetProperty("client-key").GetString() == "mock-client-key", "Wrong saved key");
        await Send(ws, new { type = "registered", payload = new Dictionary<string, string> { ["client-key"] = "mock-client-key" } });
    }
    await Scenario(ws => Register(ws, false), () => client.PairAsync(token));
    Assert(saves == 1 && settings.ClientKey == "mock-client-key", "Pairing was not saved");
    Assert(settings.CertificateHash == Convert.ToHexString(SHA256.HashData(certificate.RawData)), "Certificate not pinned");
    async Task Switch(WebSocket ws, bool success)
    {
        await Register(ws, true);
        var command = await Read(ws);
        Assert(command.GetProperty("uri").GetString() == "ssap://tv/switchInput", "Wrong SSAP command");
        Assert(command.GetProperty("payload").GetProperty("inputId").GetString() == "HDMI_3", "Wrong HDMI");
        // Unrelated notifications must not be interpreted as the response.
        await Send(ws, new { id = "unrelated", type = "response", payload = new { returnValue = true } });
        await Send(ws, new { id = command.GetProperty("id").GetString(), type = "response", payload = new { returnValue = success } });
    }
    await Scenario(ws => Switch(ws, true), () => client.ShowPcAsync(token));
    await Scenario(ws => Switch(ws, false), () => Reject(() => client.ShowPcAsync(token), "Negative HDMI response accepted"));
    await Scenario(async ws =>
    {
        await Read(ws);
        await Send(ws, new { type = "response", payload = new { pairingType = "PROMPT" } });
    }, () => Reject(() => client.ShowPcAsync(token), "Unattended re-pairing accepted"));
    // 404 on the legacy HDMI API must fall back to the fixed HDMI launcher app.
    await Scenario(async ws =>
    {
        await Register(ws, true);
        var first = await Read(ws);
        await Send(ws, new { id = first.GetProperty("id").GetString(), type = "error", error = "404 no such service" });
        var fallback = await Read(ws);
        Assert(fallback.GetProperty("uri").GetString() == "ssap://system.launcher/launch", "No launcher fallback");
        Assert(fallback.GetProperty("payload").GetProperty("id").GetString() == "com.webos.app.hdmi3", "Fallback selected a different HDMI");
        Assert(first.GetProperty("id").GetString() != fallback.GetProperty("id").GetString(), "Request IDs were reused");
        await Send(ws, new { id = fallback.GetProperty("id").GetString(), type = "response", payload = new { returnValue = true } });
    }, () => client.ShowPcAsync(token));

    // Authentication errors are not silently retried or paired again; do not expose raw payload/key.
    await Scenario(async ws =>
    {
        await Read(ws);
        await Send(ws, new { id = "register", type = "error", error = "401 insufficient permissions " + settings.ClientKey });
    }, async () =>
    {
        try { await client.ShowPcAsync(token); throw new Exception("Registration denial accepted"); }
        catch (LgTvProtocolException ex)
        {
            Assert(ex.Code == 401 && ex.Stage.Contains("регистрация"), "Registration phase/code lost");
            Assert(!ex.Message.Contains(settings.ClientKey!), "Client key leaked in diagnostics");
        }
    });

    await Scenario(async ws =>
    {
        await Register(ws, true);
        var first = await Read(ws);
        await Send(ws, new { id = first.GetProperty("id").GetString(), type = "error", error = "403 access denied" });
        var fallback = await Read(ws);
        await Send(ws, new { id = fallback.GetProperty("id").GetString(), type = "error", error = "401 insufficient permissions" });
    }, async () =>
    {
        try { await client.ShowPcAsync(token); throw new Exception("HDMI permission denial accepted"); }
        catch (LgTvProtocolException ex) { Assert(ex.Code == 401 && ex.Stage == "system.launcher/launch", "HDMI error phase/code lost"); }
    });

    var sessions = 0;
    await Scenario(async ws =>
    {
        if (++sessions == 1)
        {
            await Read(ws);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "TV starting", token);
        }
        else await Switch(ws, true);
    }, () => client.ShowPcAsync(token), connections: 2);
    Assert(wakes == 1 && sessions == 2, "A closing socket during boot was not retried exactly once");
    Assert(saves == 1, "Normal command rewrote pairing credentials");
}
finally { listener.Stop(); }
Console.WriteLine("TV tests passed: LAN validation, magic packet, TLS pairing and pin, saved key, HDMI selection, errors and re-pair rejection.");
