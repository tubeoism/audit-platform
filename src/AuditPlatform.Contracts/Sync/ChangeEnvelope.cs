namespace AuditPlatform.Contracts.Sync;

/// <summary>
/// One change produced by a client, transported as an opaque payload so the wire format does not depend on the
/// sync model still under evaluation (field-level versioning vs CRDT, see ADR-0004).
/// </summary>
/// <param name="ChangeId">Client-generated, globally unique; makes delivery idempotent.</param>
/// <param name="EngagementId">Engagement the change belongs to; authorisation and lock are checked per engagement.</param>
/// <param name="DocumentId">Working paper (or other synced document) the change applies to.</param>
/// <param name="AuthorUserId">Authenticated author; the master overwrites it with the identity on the connection.</param>
/// <param name="ClientId">Installation that produced the change.</param>
/// <param name="CreatedAt">Client clock at creation; informational only, never used for ordering on the master.</param>
/// <param name="PayloadKind">Identifies the sync engine format, e.g. "field-v1" or "yrs-update-v1".</param>
/// <param name="Payload">Engine-specific bytes.</param>
public sealed record ChangeEnvelope(
    Guid ChangeId,
    Guid EngagementId,
    Guid DocumentId,
    Guid AuthorUserId,
    Guid ClientId,
    DateTimeOffset CreatedAt,
    string PayloadKind,
    byte[] Payload);

/// <summary>Monotonic position in the master's per-engagement change sequence.</summary>
public readonly record struct SyncCursor(long Sequence)
{
    public static SyncCursor Start => new(0);
}

public enum ChangeOutcome
{
    Accepted = 0,
    Duplicate = 1,
    Conflict = 2,
    Rejected = 3,
}

public sealed record ChangeResult(Guid ChangeId, ChangeOutcome Outcome, string? Reason = null);

public sealed record PushResult(IReadOnlyList<ChangeResult> Results, SyncCursor Cursor);

public sealed record PullResult(IReadOnlyList<ChangeEnvelope> Changes, SyncCursor Cursor, bool HasMore);
