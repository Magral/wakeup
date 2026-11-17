// Source-independent power policy. Never vetoes sleep/shutdown and never owns the TV's power state.
internal sealed class TvPowerCoordinator(Func<bool> enabled, Func<int, CancellationToken, Task> select,
    Action cancelOther, Action<string> log) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private CancellationTokenSource? current;
    private Task work = Task.CompletedTask;
    private bool down, upRequested, downConfirmed, disposed;
    internal bool GoingDown { get { lock (gate) return down && enabled(); } }
    internal Task ResumeAsync()
    {
        lock (gate)
        {
            if (disposed || !enabled()) return Task.CompletedTask;
            if (upRequested && !down) return work;
            down = false; downConfirmed = false; upRequested = true;
            return Schedule(1, TimeSpan.FromSeconds(65));
        }
    }
    internal Task PrepareDownAsync(TimeSpan budget)
    {
        lock (gate)
        {
            if (disposed || !enabled()) return Task.CompletedTask;
            if (down && downConfirmed) return Task.CompletedTask;
            if (down && current != null) return work;
            down = true;
            return Schedule(2, budget);
        }
    }
    private Task Schedule(int input, TimeSpan budget)
    {
        var previous = current;
        var cancellation = new CancellationTokenSource(budget);
        current = cancellation;
        work = Task.Run(async () =>
        {
            try
            {
                TryCancel(previous);
                cancellation.Token.ThrowIfCancellationRequested();
                if (input == 2) cancelOther();
                await serial.WaitAsync(cancellation.Token);
                try { cancellation.Token.ThrowIfCancellationRequested(); await select(input, cancellation.Token); }
                finally { serial.Release(); }
                lock (gate) if (current == cancellation && input == 2) downConfirmed = true;
                log($"Питание Windows: ТВ подтвердил HDMI {input}.");
            }
            catch (OperationCanceledException) { log($"Питание Windows: HDMI {input} — отмена или предел ожидания; переход Windows не блокируется."); }
            catch (Exception ex) { log($"Питание Windows: HDMI {input} не подтверждён: {ex.Message}"); }
            finally
            {
                lock (gate) if (current == cancellation) current = null;
                cancellation.Dispose();
            }
        });
        return work;
    }
    private static void TryCancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    internal void CancelPending()
    {
        CancellationTokenSource? pending;
        lock (gate) pending = down ? null : current;
        TryCancel(pending);
    }
    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (gate) { disposed = true; pending = current; }
        TryCancel(pending);
    }
}
