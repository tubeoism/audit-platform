namespace AuditPlatform.AuditLog;

/// <summary>Content of an audit event before it is sealed into the chain.</summary>
public sealed record AuditEvent(
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    string Action,
    string EntityType,
    string EntityId,
    Guid? EngagementId,
    string PayloadJson);

/// <summary>
/// A sealed audit log entry. <see cref="Hash"/> = SHA-256(<see cref="PreviousHash"/> || canonical(event)),
/// so altering, inserting or deleting any earlier entry breaks every later hash.
/// </summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    string Action,
    string EntityType,
    string EntityId,
    Guid? EngagementId,
    string PayloadJson,
    string PreviousHash,
    string Hash);
