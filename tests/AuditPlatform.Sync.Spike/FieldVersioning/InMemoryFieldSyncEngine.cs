using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Sync.Spike.FieldVersioning;

/// <summary>
/// Candidate (a): field-level versioning with master arbitration. The master orders all changes, decides each one
/// with <see cref="FieldDocument"/>, and serves the ordered log back. State lives in memory: this is the spike, the
/// durable store comes with the implementation.
/// </summary>
public sealed class InMemoryFieldSyncEngine(TimeProvider clock) : IDocumentSyncEngine
{
    private static readonly Guid MasterClientId = Guid.Empty;
    private readonly Dictionary<Guid, EngagementState> _engagements = [];
    private readonly Lock _gate = new();

    public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string> { FieldPayload.Kind };

    public ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_gate)
        {
            var state = State(engagementId);
            var results = new List<ChangeResult>(changes.Count);
            foreach (var change in changes)
            {
                results.Add(Admit(state, change, fromMaster: false));
            }

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

    /// <summary>Online-only: the caller has passed the TOTP step-up (ADR-0005). The master computes the hash it seals.</summary>
    public ChangeResult SignOff(Guid engagementId, Guid documentId, EngagementRole level, Guid reviewer)
    {
        lock (_gate)
        {
            var state = State(engagementId);
            var hash = state.Document(documentId, engagementId).ComputeContentHash();
            return Admit(state, MasterEnvelope(engagementId, documentId, reviewer, new SignOffPaper(level.ToString(), hash)), fromMaster: true);
        }
    }

    public FieldDocument Document(Guid engagementId, Guid documentId)
    {
        lock (_gate)
        {
            return State(engagementId).Document(documentId, engagementId);
        }
    }

    public IReadOnlyList<Guid> DocumentIds(Guid engagementId)
    {
        lock (_gate)
        {
            return [.. State(engagementId).Docs.Keys.Order()];
        }
    }

    public int LogLength(Guid engagementId)
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
            // A redelivery must not turn a flagged conflict or a rejection into "delivered".
            return earlier.Outcome == ChangeOutcome.Accepted ? earlier with { Outcome = ChangeOutcome.Duplicate } : earlier;
        }

        ChangeResult result;
        FieldOp op;
        try
        {
            op = FieldPayload.Decode(change.Payload);
        }
        catch (System.Text.Json.JsonException e)
        {
            result = new ChangeResult(change.ChangeId, ChangeOutcome.Rejected, $"Malformed payload: {e.Message}");
            state.Seen[change.ChangeId] = result;
            return result;
        }

        if (op.MasterOnly && !fromMaster)
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
                // Accepted changes and flagged conflicts are both part of the shared record; rejections are not.
                state.Log.Add(change);
            }
        }

        state.Seen[change.ChangeId] = result;
        return result;
    }

    private ChangeEnvelope MasterEnvelope(Guid engagementId, Guid documentId, Guid actor, FieldOp op) =>
        new(Guid.NewGuid(), engagementId, documentId, actor, MasterClientId, clock.GetUtcNow(), FieldPayload.Kind, FieldPayload.Encode(op));

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
