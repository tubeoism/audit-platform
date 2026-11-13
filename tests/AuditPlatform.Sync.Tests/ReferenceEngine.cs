using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Sync;

namespace AuditPlatform.Sync.Tests;

/// <summary>
/// Reference model of the master: the rules of ADR-0004 over an in-memory log, in about a hundred lines. The durable
/// PostgreSQL engine must behave exactly like it; the scenarios run against both and S11 compares them change for change.
/// </summary>
internal sealed class ReferenceEngine : IDocumentSyncEngine
{
    private readonly Dictionary<Guid, EngagementState> _engagements = [];
    private readonly Lock _gate = new();

    public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string> { FieldPayload.Kind };

    public ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var state = State(engagementId);
            var results = changes.Select(change => Admit(state, change, fromMaster: false)).ToList();
            return ValueTask.FromResult(new PushResult(results, new SyncCursor(state.Log.Count)));
        }
    }

    public ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var log = State(engagementId).Log;
            var from = (int)Math.Clamp(since.Sequence, 0, log.Count);
            var page = log.Skip(from).Take(maxChanges).ToList();
            var cursor = new SyncCursor(from + page.Count);
            return ValueTask.FromResult(new PullResult(page, cursor, cursor.Sequence < log.Count));
        }
    }

    public SignOffAttempt SignOff(Guid engagementId, Guid documentId, EngagementRole level, Guid reviewer)
    {
        lock (_gate)
        {
            var state = State(engagementId);
            var doc = state.Document(documentId, engagementId);
            if (doc.WhySignOffIsRefused(level.ToString(), reviewer) is { } refusal)
            {
                return new SignOffAttempt(SignOffStatus.Refused, refusal, Guid.Empty, null, new SyncCursor(state.Log.Count));
            }

            var hash = doc.ComputeContentHash();
            var envelope = new ChangeEnvelope(Guid.NewGuid(), engagementId, documentId, reviewer, Guid.Empty, DateTimeOffset.UtcNow,
                FieldPayload.Kind, FieldPayload.Encode(new SignOffPaper(level.ToString(), hash)));
            Admit(state, envelope, fromMaster: true);
            return new SignOffAttempt(SignOffStatus.SignedOff, null, envelope.ChangeId, hash, new SyncCursor(state.Log.Count));
        }
    }

    public FieldDocument Document(Guid engagementId, Guid documentId)
    {
        lock (_gate)
        {
            return State(engagementId).Document(documentId, engagementId);
        }
    }

    public FieldDocument Replay(Guid engagementId, Guid documentId)
    {
        lock (_gate)
        {
            var doc = new FieldDocument(documentId, engagementId);
            foreach (var change in State(engagementId).Log.Where(c => c.DocumentId == documentId))
            {
                doc.Apply(change.ChangeId, change.AuthorUserId, FieldPayload.Decode(change.Payload));
            }

            return doc;
        }
    }

    public long Head(Guid engagementId)
    {
        lock (_gate)
        {
            return State(engagementId).Log.Count;
        }
    }

    private static ChangeResult Admit(EngagementState state, ChangeEnvelope change, bool fromMaster)
    {
        if (state.Seen.TryGetValue(change.ChangeId, out var earlier))
        {
            return earlier.Outcome == ChangeOutcome.Accepted ? earlier with { Outcome = ChangeOutcome.Duplicate } : earlier;
        }

        ChangeResult result;
        FieldOp? op = null;
        try
        {
            op = FieldPayload.Decode(change.Payload);
        }
        catch (System.Text.Json.JsonException)
        {
            // Decided below.
        }

        if (op is null)
        {
            result = new ChangeResult(change.ChangeId, ChangeOutcome.Rejected, "Malformed field-v1 payload.");
        }
        else if (op.MasterOnly && !fromMaster)
        {
            result = new ChangeResult(change.ChangeId, ChangeOutcome.Rejected, "This operation needs an online, stepped-up session on the master.");
        }
        else
        {
            var decided = state.Document(change.DocumentId, change.EngagementId).Apply(change.ChangeId, change.AuthorUserId, op);
            result = new ChangeResult(change.ChangeId, decided.Decision switch
            {
                Decision.Accepted => ChangeOutcome.Accepted,
                Decision.Conflict => ChangeOutcome.Conflict,
                _ => ChangeOutcome.Rejected,
            }, decided.Reason);
            if (decided.Decision != Decision.Rejected)
            {
                state.Log.Add(change with { Sequence = state.Log.Count + 1, ReceivedAt = DateTimeOffset.UtcNow });
            }
        }

        state.Seen[change.ChangeId] = result;
        return result;
    }

    private EngagementState State(Guid engagementId)
    {
        if (!_engagements.TryGetValue(engagementId, out var state))
        {
            _engagements[engagementId] = state = new EngagementState();
        }

        return state;
    }

    private sealed class EngagementState
    {
        public List<ChangeEnvelope> Log { get; } = [];
        public Dictionary<Guid, ChangeResult> Seen { get; } = [];
        public Dictionary<Guid, FieldDocument> Docs { get; } = [];

        public FieldDocument Document(Guid documentId, Guid engagementId)
        {
            if (!Docs.TryGetValue(documentId, out var doc))
            {
                Docs[documentId] = doc = new FieldDocument(documentId, engagementId);
            }

            return doc;
        }
    }
}
