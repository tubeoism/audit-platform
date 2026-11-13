namespace AuditPlatform.Sync.Spike.Crdt;

/// <summary>
/// Control for candidate (b): the simplest CRDT one could write in plain .NET, a last-writer-wins map ordered by the
/// author's wall clock (ties broken by client id). Included to show what clock-based merging does on a skewed laptop.
/// </summary>
public sealed class LwwFieldMap(Guid clientId, TimeProvider clock)
{
    private readonly Dictionary<string, LwwEntry> _entries = new(StringComparer.Ordinal);

    public void Set(string key, string value) => Merge(key, new LwwEntry(value, clock.GetUtcNow(), clientId));

    public string? Get(string key) => _entries.TryGetValue(key, out var e) ? e.Value : null;

    public void MergeFrom(LwwFieldMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (key, entry) in other._entries)
        {
            Merge(key, entry);
        }
    }

    private void Merge(string key, LwwEntry incoming)
    {
        if (!_entries.TryGetValue(key, out var current)
            || incoming.At > current.At
            || (incoming.At == current.At && incoming.ClientId.CompareTo(current.ClientId) > 0))
        {
            _entries[key] = incoming;
        }
    }

    private sealed record LwwEntry(string Value, DateTimeOffset At, Guid ClientId);
}
