using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Master.Data.Entities;

// Persistence rows are kept separate from domain aggregates so the schema can evolve (and be migrated)
// without leaking EF concerns into the domain. Mapping lives in repositories added with each feature.

public sealed class UserRow
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EngagementRow
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string ClientName { get; set; }
    public required string ContractReference { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public EngagementStatus Status { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public Guid? LockedBy { get; set; }
    public List<EngagementMemberRow> Members { get; set; } = [];
}

public sealed class EngagementMemberRow
{
    public Guid EngagementId { get; set; }
    public Guid UserId { get; set; }
    public EngagementRole Role { get; set; }
}

/// <summary>Methodology library entry, imported from ia-aup-procedures. Key: code + version + language.</summary>
public sealed class ProcedureTemplateRow
{
    public required string Code { get; set; }
    public int Version { get; set; }
    public required string Language { get; set; }
    public required string AreaCode { get; set; }
    public required string AreaName { get; set; }
    public required string Title { get; set; }
    public required string Procedure { get; set; }
    public List<string> AuditContent { get; set; } = [];
    public List<string> EvidenceToCheck { get; set; } = [];
    public bool IsApproved { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

/// <summary>
/// Engine-neutral change journal: every accepted change, in master order per engagement.
/// The sync engine (ADR-0004) may keep additional materialised state of its own.
/// </summary>
public sealed class ChangeRow
{
    public Guid EngagementId { get; set; }
    public long Sequence { get; set; }
    public Guid ChangeId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid AuthorUserId { get; set; }
    public Guid ClientId { get; set; }
    public DateTimeOffset ClientCreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string PayloadKind { get; set; }
    public required byte[] Payload { get; set; }
}

public sealed class AuditLogRow
{
    public long Sequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public Guid? EngagementId { get; set; }
    public required string PayloadJson { get; set; }
    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}

public sealed class EvidenceObjectRow
{
    public required string Hash { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset FirstReceivedAt { get; set; }
}

public sealed class EvidenceIntakeRow
{
    public Guid Id { get; set; }
    public required string Hash { get; set; }
    public Guid EngagementId { get; set; }
    public required string OriginalFileName { get; set; }
    public string? MediaType { get; set; }
    public Guid ReceivedBy { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string Source { get; set; }
}
