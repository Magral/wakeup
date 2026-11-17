// All HDMI selectors, including manual requests, share InputGate. The warm socket
// uses the existing saved key and certificate pin, never an unattended pairing prompt.
internal sealed class LgPowerInputTransport(Func<LgTvSettings> read, Action<string> log) : IDisposable
{
    internal static readonly SemaphoreSlim InputGate = new(1, 1);
    private LgWebOsClient.InputSession? session;
    private (string Address, bool Secure, string? Key, string? Pin)? identity;
    private DateTime warmedAt;
    private readonly object warmGate = new();
    private CancellationTokenSource? warmDeadline;
    internal void CancelWarmup() { lock (warmGate) warmDeadline?.Cancel(); }
    internal async Task SelectAsync(int input, CancellationToken token)
    {
        await InputGate.WaitAsync(token);
        try
        {
            var settings = read(); settings.Validate(); settings.Hdmi = input;
            if (!settings.PowerInputSync) throw new OperationCanceledException("Синхронизация отключена.");
            DropIfChanged(settings);
            if (input == 1)
            {
                // TV may be off and the network may still be resuming.
                await new LgWebOsClient(settings, _ => {}).ShowPcAsync(token);
                try { await WarmAsync(settings, token); }
                catch (Exception ex) when (!token.IsCancellationRequested) { log("Подготовка соединения LG: " + ex.Message); }
                return;
            }
            if (session?.Connected == true)
            {
                try { await session.SelectAsync(input, token); return; }
                catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException
                    && !token.IsCancellationRequested) { Drop(); }
                catch { Drop(); throw; }
            }
            // One authenticated connection attempt only; never wakes a sleeping TV to HDMI2.
            await WarmAsync(settings, token);
            try { await session!.SelectAsync(input, token); }
            catch { Drop(); throw; }
        }
        finally { InputGate.Release(); }
    }
    internal async Task MaintainAsync(Func<bool> active, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                if (!active()) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                lock (warmGate) warmDeadline = deadline;
                bool entered = false;
                try
                {
                    await InputGate.WaitAsync(deadline.Token); entered = true;
                    var settings = read();
                    if (!active() || !settings.PowerInputSync || string.IsNullOrWhiteSpace(settings.ClientKey)) { Drop(); continue; }
                    DropIfChanged(settings);
                    if (session?.Connected == true && DateTime.UtcNow - warmedAt < TimeSpan.FromSeconds(60)) continue;
                    await WarmAsync(settings, deadline.Token);
                }
                catch (Exception) when (!token.IsCancellationRequested) { /* TV may deliberately be off. */ }
                finally
                {
                    lock (warmGate) if (warmDeadline == deadline) warmDeadline = null;
                    if (entered) InputGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await InputGate.WaitAsync();
            try { Drop(); } finally { InputGate.Release(); }
        }
    }
    private void DropIfChanged(LgTvSettings settings)
    {
        var next = (settings.Address, settings.Secure, settings.ClientKey, settings.CertificateHash);
        if (identity != next) { Drop(); identity = next; }
    }
    private async Task WarmAsync(LgTvSettings settings, CancellationToken token)
    {
        var next = await new LgWebOsClient(settings, _ => {}).OpenInputSessionAsync(token);
        Drop();
        session = next;
        warmedAt = DateTime.UtcNow;
    }
    private void Drop() { session?.Dispose(); session = null; }
    // The lifetime owner cancels/awaits MaintainAsync before disposing the transport.
    public void Dispose() => Drop();
}
