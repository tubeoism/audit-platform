namespace AuditPlatform.Contracts.Sync;

/// <summary>
/// One change, transported as an opaque payload so the wire format does not depend on the sync engine
/// (ADR-0004: field-level versioning, payload kind "field-v1").
/// </summary>
/// <param name="ChangeId">Client-generated, globally unique; makes delivery idempotent.</param>
/// <param name="EngagementId">Engagement the change belongs to; authorisation and lock are checked per engagement.</param>
/// <param name="DocumentId">Working paper (or other synced document) the change applies to.</param>
/// <param name="AuthorUserId">Authenticated author; the master overwrites it with the identity on the connection.</param>
/// <param name="ClientId">Installation that produced the change.</param>
/// <param name="CreatedAt">Client clock at creation; informational only, never used for ordering on the master.</param>
/// <param name="PayloadKind">Identifies the sync engine format, e.g. "field-v1".</param>
/// <param name="Payload">Engine-specific bytes.</param>
/// <param name="Sequence">Position in the master's log of the engagement; set by the master on pull, 0 on push.</param>
/// <param name="ReceivedAt">Master clock when the change entered the log; set by the master on pull.</param>
public sealed record ChangeEnvelope(
    Guid ChangeId,
    Guid EngagementId,
    Guid DocumentId,
    Guid AuthorUserId,
    Guid ClientId,
    DateTimeOffset CreatedAt,
    string PayloadKind,
    byte[] Payload,
    long Sequence = 0,
    DateTimeOffset? ReceivedAt = null);

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

/// <summary>HTTP routes of the master's sync API that are not hub methods.</summary>
public static class SyncRoutes
{
    public const string SignOff = "/api/sync/sign-off";
}

/// <summary>
/// Signs a working paper off at one review level. Online only: <paramref name="StepUpToken"/> is the single-use grant
/// bought with a fresh TOTP code for exactly this working paper at the content hash the reviewer was looking at
/// (ADR-0005). If the master holds different content, the grant does not match and nothing is signed.
/// </summary>
/// <param name="Level">"ManagerQa" or "JapanDesk".</param>
public sealed record SignOffRequest(Guid EngagementId, Guid WorkingPaperId, string Level, string StepUpToken);

/// <param name="ContentHash">The hash the master sealed.</param>
public sealed record SignOffResponse(Guid ChangeId, string ContentHash, SyncCursor Cursor);

/// <summary>Error body of the sync API. <see cref="Error"/> is a stable machine-readable code.</summary>
public sealed record SyncError(string Error, string Message);
