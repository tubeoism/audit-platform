using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Sync.Spike.FieldVersioning;

public enum PendingState
{
    Pending = 0,
    Conflict = 1,
    Rejected = 2,
}

public sealed class PendingChange(ChangeEnvelope envelope, FieldOp op)
{
    public ChangeEnvelope Envelope { get; } = envelope;
    public FieldOp Op { get; } = op;
    public PendingState State { get; set; }
    public string? Reason { get; set; }
}

/// <summary>What the transport does to one synchronisation, to model a flaky connection.</summary>
public enum SyncFault
{
    None = 0,

    /// <summary>The master applies the push but the answer never arrives; the client will push the same changes again.</summary>
    LostAck = 1,

    /// <summary>The same batch is delivered twice in a row.</summary>
    DeliveredTwice = 2,
}

/// <summary>
/// Simulated laptop for candidate (a): a confirmed replica rebuilt from the master's log, plus an outbox of local
/// changes. Nothing leaves the outbox unless the master accepted it; conflicts and rejections stay visible to the author.
/// </summary>
public sealed class FieldClient(Guid userId, Guid engagementId, TimeProvider clock)
{
    private readonly Dictionary<Guid, FieldDocument> _confirmed = [];
    private readonly List<PendingChange> _outbox = [];
    private long _cursor;

    public Guid UserId { get; } = userId;
    public Guid ClientId { get; } = Guid.NewGuid();
    public IReadOnlyList<PendingChange> Outbox => _outbox;
    public IEnumerable<PendingChange> NeedsAttention => _outbox.Where(p => p.State != PendingState.Pending);

    public FieldDocument Confirmed(Guid documentId)
    {
        if (!_confirmed.TryGetValue(documentId, out var doc))
        {
            _confirmed[documentId] = doc = new FieldDocument(documentId, engagementId);
        }

        return doc;
    }

    /// <summary>What the auditor sees: their own unsent edit if there is one, otherwise the confirmed value.</summary>
    public string? View(Guid documentId, string key) =>
        LastPendingSet(documentId, key)?.Value ?? Confirmed(documentId).Value(key);

    public Guid CreatePaper(Guid documentId, string templateCode, int templateVersion, Guid preparerId) =>
        Enqueue(documentId, new CreatePaper(templateCode, templateVersion, preparerId));

    public Guid SetField(Guid documentId, string key, string value)
    {
        // Chain on the author's own unsent edit, so a series of offline saves of one field is not a conflict with itself.
        var mine = _outbox.LastOrDefault(p => p.State == PendingState.Pending && p.Envelope.DocumentId == documentId && p.Op is SetField s && s.Key == key);
        Guid? baseChange = mine?.Envelope.ChangeId ?? (Confirmed(documentId).Fields.TryGetValue(key, out var cell) ? cell.ChangeId : null);
        return Enqueue(documentId, new SetField(key, value, baseChange));
    }

    public Guid Resolve(Guid documentId, Guid conflictId, string value)
    {
        var doc = Confirmed(documentId);
        var key = doc.Conflicts[conflictId].Key;
        return Enqueue(documentId, new ResolveConflict(conflictId, value, doc.Fields.TryGetValue(key, out var cell) ? cell.ChangeId : null));
    }

    public Guid LinkEvidence(Guid documentId, string hash) => Enqueue(documentId, new LinkEvidence(hash));

    public Guid UnlinkEvidence(Guid documentId, string hash) => Enqueue(documentId, new UnlinkEvidence(hash));

    public Guid RaiseNote(Guid documentId, Guid noteId, string fieldKey, string text) => Enqueue(documentId, new RaiseNote(noteId, fieldKey, text));

    public Guid Respond(Guid documentId, Guid noteId, string response) => Enqueue(documentId, new RespondNote(noteId, response));

    public Guid ClearNote(Guid documentId, Guid noteId) =>
        Enqueue(documentId, new ClearNote(noteId, Confirmed(documentId).Notes[noteId].Responses[^1].ChangeId));

    public Guid Submit(Guid documentId) => Enqueue(documentId, new SubmitForReview());

    /// <summary>Enqueues any operation as-is; used to show that the master refuses master-only operations from a client.</summary>
    public Guid EnqueueRaw(Guid documentId, FieldOp op) => Enqueue(documentId, op);

    public async Task SyncAsync(IDocumentSyncEngine engine, SyncFault fault = SyncFault.None)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var ct = TestContext.Current.CancellationToken;
        var batch = _outbox.Where(p => p.State == PendingState.Pending).ToList();
        if (batch.Count > 0)
        {
            var envelopes = batch.Select(p => p.Envelope).ToList();
            var result = await engine.ApplyAsync(engagementId, envelopes, ct);
            if (fault == SyncFault.DeliveredTwice)
            {
                result = await engine.ApplyAsync(engagementId, envelopes, ct);
            }

            if (fault != SyncFault.LostAck)
            {
                ProcessResults(batch, result);
            }
        }

        PullResult page;
        do
        {
            page = await engine.ReadSinceAsync(engagementId, new SyncCursor(_cursor), 500, ct);
            foreach (var change in page.Changes)
            {
                var decided = Confirmed(change.DocumentId).Apply(change.ChangeId, change.AuthorUserId, FieldPayload.Decode(change.Payload));
                if (decided.Decision == Decision.Rejected)
                {
                    throw new InvalidOperationException($"Replica diverged from the master on change {change.ChangeId}: {decided.Reason}");
                }
            }

            _cursor = page.Cursor.Sequence;
        }
        while (page.HasMore);

        // A flagged conflict leaves the author's list once somebody resolved it (or the author superseded it).
        _outbox.RemoveAll(p => p.State == PendingState.Conflict
            && Confirmed(p.Envelope.DocumentId).Conflicts.TryGetValue(p.Envelope.ChangeId, out var c) && c.Status != ConflictStatus.Open);
    }

    private void ProcessResults(List<PendingChange> batch, PushResult result)
    {
        var byId = batch.ToDictionary(p => p.Envelope.ChangeId);
        foreach (var r in result.Results)
        {
            var pending = byId[r.ChangeId];
            switch (r.Outcome)
            {
                case ChangeOutcome.Accepted:
                case ChangeOutcome.Duplicate:
                    _outbox.Remove(pending);
                    break;
                case ChangeOutcome.Conflict:
                    pending.State = PendingState.Conflict;
                    pending.Reason = r.Reason;
                    break;
                default:
                    pending.State = PendingState.Rejected;
                    pending.Reason = r.Reason;
                    break;
            }
        }
    }

    private SetField? LastPendingSet(Guid documentId, string key) =>
        _outbox.Where(p => p.State == PendingState.Pending && p.Envelope.DocumentId == documentId)
            .Select(p => p.Op).OfType<SetField>().LastOrDefault(s => s.Key == key);

    private Guid Enqueue(Guid documentId, FieldOp op)
    {
        var envelope = new ChangeEnvelope(Guid.NewGuid(), engagementId, documentId, UserId, ClientId, clock.GetUtcNow(), FieldPayload.Kind, FieldPayload.Encode(op));
        _outbox.Add(new PendingChange(envelope, op));
        return envelope.ChangeId;
    }
}
