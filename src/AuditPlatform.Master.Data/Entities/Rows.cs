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

    // Authentication (ADR-0005). A user can sign in only when PasswordHash and TotpEnrolledAt are both set.
    public SystemRole Role { get; set; }
    public string? PasswordHash { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>TOTP secret, encrypted with the master's secret-protection key. Pending until <see cref="TotpEnrolledAt"/> is set.</summary>
    public string? TotpSecret { get; set; }
    public DateTimeOffset? TotpEnrolledAt { get; set; }

    /// <summary>Last accepted TOTP time step; a code from this step or earlier is refused (replay protection).</summary>
    public long TotpLastStep { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>System-wide role. Separate from <see cref="EngagementRole"/>: an admin manages accounts but has no implicit engagement or sign-off rights.</summary>
public enum SystemRole
{
    User = 0,
    Admin = 1,
}

public enum UserTokenPurpose
{
    Invite = 0,
    PasswordReset = 1,
    MfaEnrol = 2,
}

/// <summary>One-time link sent by e-mail. Only a keyed hash of the token is stored.</summary>
public sealed class UserTokenRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public UserTokenPurpose Purpose { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid CreatedBy { get; set; }
}

public sealed class RecoveryCodeRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>One sign-in on one client installation. Revoking the session refuses its access and refresh tokens at once.</summary>
public sealed class SessionRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid InstallationId { get; set; }
    public required string DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastRefreshedAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>Rotating refresh token of a session. Presenting a token that was already rotated revokes the session.</summary>
public sealed class RefreshTokenRow
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? ReplacedBy { get; set; }
}

/// <summary>Single-use proof of a fresh TOTP code, bound to one working paper at one content hash (sign-off step-up).</summary>
public sealed class StepUpGrantRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public required string TokenHash { get; set; }
    public required string Purpose { get; set; }
    public Guid WorkingPaperId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
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
