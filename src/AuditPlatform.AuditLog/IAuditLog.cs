namespace AuditPlatform.AuditLog;

/// <summary>
/// Append-only audit log. Implementations must serialise appends so each entry chains to the one before it,
/// and must offer no update or delete path (the master's PostgreSQL table also blocks UPDATE/DELETE by trigger).
/// </summary>
public interface IAuditLog
{
    ValueTask<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>Appends several events as consecutive entries, in the order given, or none of them.</summary>
    ValueTask<IReadOnlyList<AuditEntry>> AppendManyAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken);
}

/// <summary>In-process implementation for tests and for the client's local log.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public ValueTask<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var previous = _entries.Count == 0 ? HashChain.GenesisHash : _entries[^1].Hash;
            var entry = HashChain.Seal(_entries.Count + 1, previous, auditEvent);
            _entries.Add(entry);
            return ValueTask.FromResult(entry);
        }
    }

    public ValueTask<IReadOnlyList<AuditEntry>> AppendManyAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);
        lock (_gate)
        {
            var added = new List<AuditEntry>(auditEvents.Count);
            foreach (var auditEvent in auditEvents)
            {
                var previous = _entries.Count == 0 ? HashChain.GenesisHash : _entries[^1].Hash;
                var entry = HashChain.Seal(_entries.Count + 1, previous, auditEvent);
                _entries.Add(entry);
                added.Add(entry);
            }

            return ValueTask.FromResult<IReadOnlyList<AuditEntry>>(added);
        }
    }
}
