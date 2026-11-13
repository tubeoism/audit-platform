using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Sync.Spike.Crdt;

/// <summary>
/// Candidate (b) on the master: every update is merged into the master's Yrs document and relayed. The merge cannot
/// fail and cannot report a conflict, so the only outcomes are Accepted and Duplicate.
/// </summary>
public sealed class YrsSyncEngine : IDocumentSyncEngine, IDisposable
{
    public const string Kind = "yrs-update-v1";

    private readonly Dictionary<Guid, YrsPaper> _docs = [];
    private readonly List<ChangeEnvelope> _log = [];
    private readonly HashSet<Guid> _seen = [];
    private readonly Lock _gate = new();

    public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string> { Kind };

    public ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_gate)
        {
            var results = new List<ChangeResult>();
            foreach (var change in changes)
            {
                if (!_seen.Add(change.ChangeId))
                {
                    results.Add(new ChangeResult(change.ChangeId, ChangeOutcome.Duplicate));
                    continue;
                }

                Document(change.DocumentId, engagementId).Apply(change.Payload);
                _log.Add(change);
                results.Add(new ChangeResult(change.ChangeId, ChangeOutcome.Accepted));
            }

            return ValueTask.FromResult(new PushResult(results, new SyncCursor(_log.Count)));
        }
    }

    public ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var from = (int)Math.Clamp(since.Sequence, 0, _log.Count);
            var page = _log.Skip(from).Take(maxChanges).ToList();
            return ValueTask.FromResult(new PullResult(page, new SyncCursor(from + page.Count), from + page.Count < _log.Count));
        }
    }

    public YrsPaper Document(Guid documentId, Guid engagementId)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(documentId, out var doc))
            {
                _docs[documentId] = doc = new YrsPaper(documentId, engagementId);
            }

            return doc;
        }
    }

    public void Dispose()
    {
        foreach (var doc in _docs.Values)
        {
            doc.Dispose();
        }
    }
}

/// <summary>Simulated laptop for candidate (b): a full local Yrs replica; a sync pushes what the master lacks and pulls the rest.</summary>
public sealed class YrsClient(Guid userId, Guid engagementId, Guid documentId) : IDisposable
{
    private byte[]? _lastPushedState;
    private long _cursor;

    public Guid UserId { get; } = userId;
    public YrsPaper Paper { get; } = new(documentId, engagementId);
    public List<ChangeResult> PushResults { get; } = [];

    public async Task SyncAsync(IDocumentSyncEngine engine, bool deliverTwice = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var ct = TestContext.Current.CancellationToken;
        var update = Paper.UpdateSince(_lastPushedState);
        var envelope = new ChangeEnvelope(Guid.NewGuid(), engagementId, documentId, UserId, Guid.Empty, DateTimeOffset.UtcNow, YrsSyncEngine.Kind, update);
        var pushed = await engine.ApplyAsync(engagementId, [envelope], ct);
        if (deliverTwice)
        {
            await engine.ApplyAsync(engagementId, [envelope with { ChangeId = Guid.NewGuid() }], ct);
        }

        PushResults.AddRange(pushed.Results);
        if (pushed.Results.All(r => r.Outcome is ChangeOutcome.Accepted or ChangeOutcome.Duplicate))
        {
            _lastPushedState = Paper.StateVector();
        }

        PullResult page;
        do
        {
            page = await engine.ReadSinceAsync(engagementId, new SyncCursor(_cursor), 500, ct);
            foreach (var change in page.Changes)
            {
                Paper.Apply(change.Payload);
            }

            _cursor = page.Cursor.Sequence;
        }
        while (page.HasMore);
    }

    public void Dispose() => Paper.Dispose();
}
