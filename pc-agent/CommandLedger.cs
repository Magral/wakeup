internal sealed class CommandLedger
{
    private sealed record Entry(string Command, string Endpoint, string Reply, long Expires);
    private readonly Dictionary<string, Entry> entries = new();
    private readonly Func<long> clock;
    internal CommandLedger(Func<long>? clock = null) => this.clock = clock ?? (() => Environment.TickCount64);
    internal string? Find(string nonce, string command, string endpoint)
    {
        Prune();
        return entries.TryGetValue(nonce, out var value) && value.Command == command && value.Endpoint == endpoint ? value.Reply : null;
    }
    internal void Remember(string nonce, string command, string endpoint, string reply)
    {
        Prune();
        if (entries.Count >= 2048) entries.Remove(entries.Keys.First());
        entries[nonce] = new(command, endpoint, reply, clock() + 30_000);
    }
    private void Prune()
    {
        foreach (var key in entries.Where(x => x.Value.Expires <= clock()).Select(x => x.Key).ToArray()) entries.Remove(key);
    }
}
