using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Client.Core.LocalStore;

public enum OutboxState
{
    Pending = 0,
    Conflict = 1,
    Rejected = 2,
}

/// <summary>
/// A local change waiting to reach the master. Survives restarts; removed once the master accepts it. A change the
/// master flagged as a conflict or rejected stays here, with the reason, until a person has dealt with it.
/// </summary>
public sealed class OutboxChangeRow
{
    /// <summary>
    /// Order in which the changes were made on this laptop. Changes are pushed in this order, never by the clock:
    /// a later save of a field refers to the earlier one, and a laptop clock can be wrong or be changed.
    /// </summary>
    public long LocalSequence { get; set; }
    public Guid ChangeId { get; set; }
    public Guid EngagementId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid AuthorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string PayloadKind { get; set; }
    public required byte[] Payload { get; set; }
    public OutboxState State { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }

    public ChangeEnvelope ToEnvelope(Guid clientId) =>
        new(ChangeId, EngagementId, DocumentId, AuthorUserId, clientId, CreatedAt, PayloadKind, Payload);
}

public sealed class SyncStateRow
{
    public Guid EngagementId { get; set; }
    public long LastPulledSequence { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
}

/// <summary>
/// The confirmed state of one working paper: the fold of the master's log up to <see cref="LastSequence"/>. It holds
/// only what the master has decided; the author's unsent edits live in the outbox and are laid over it for display.
/// </summary>
public sealed class LocalDocumentRow
{
    public Guid DocumentId { get; set; }
    public Guid EngagementId { get; set; }
    public required string TemplateCode { get; set; }
    public required string Status { get; set; }
    public required string ContentHash { get; set; }
    public int OpenConflicts { get; set; }
    public long LastSequence { get; set; }

    /// <summary>The reducer's state as JSON.</summary>
    public required string State { get; set; }
}

/// <summary>The master's change log as pulled, in master order. The documents can be rebuilt from it at any time.</summary>
public sealed class ReplicaChangeRow
{
    public Guid EngagementId { get; set; }
    public long Sequence { get; set; }
    public Guid ChangeId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid AuthorUserId { get; set; }
    public Guid ClientId { get; set; }
    public DateTimeOffset ClientCreatedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public required string PayloadKind { get; set; }
    public required byte[] Payload { get; set; }
}

/// <summary>Replica header for an engagement assigned to this user.</summary>
public sealed class LocalEngagementRow
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string ClientName { get; set; }
    public EngagementStatus Status { get; set; }
    public bool IsReadOnly { get; set; }
    public string ContractReference { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
}

/// <summary>
/// A member of an engagement as the master last listed them: the name to show next to a change, and the roles that
/// decide what this laptop offers its own user. The master decides again when the change arrives.
/// </summary>
public sealed class LocalMemberRow
{
    public Guid EngagementId { get; set; }
    public Guid UserId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Names of <see cref="EngagementRole"/> values, comma-separated.</summary>
    public required string Roles { get; set; }

    public IReadOnlySet<EngagementRole> RoleSet() =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => Enum.TryParse<EngagementRole>(r, out var role) ? role : (EngagementRole?)null)
            .OfType<EngagementRole>()
            .ToHashSet();
}

/// <summary>One version of a procedure from the methodology library, cached so forms work offline.</summary>
public sealed class LocalTemplateRow
{
    public required string Code { get; set; }
    public int Version { get; set; }
    public required string Language { get; set; }
    public required string AreaCode { get; set; }
    public required string AreaName { get; set; }
    public required string Title { get; set; }
    public required string Procedure { get; set; }

    /// <summary>JSON array of the audit-content steps.</summary>
    public required string AuditContent { get; set; }

    /// <summary>JSON array of the evidence to check.</summary>
    public required string EvidenceToCheck { get; set; }
}

public enum EvidenceUpload
{
    /// <summary>Attached on this laptop; the master does not hold the file yet.</summary>
    Pending = 0,

    /// <summary>The master holds the file.</summary>
    AtMaster = 1,

    /// <summary>The master refused the file; <see cref="LocalEvidenceRow.LastError"/> says why. Never sent again.</summary>
    Refused = 2,
}

/// <summary>
/// An evidence file of an engagement as this laptop knows it: attached here, or listed by the master. The bytes are
/// in the content-addressed store beside the database, under <see cref="Hash"/>, when this laptop has them at all.
/// </summary>
public sealed class LocalEvidenceRow
{
    public Guid EngagementId { get; set; }
    public required string Hash { get; set; }
    public required string FileName { get; set; }
    public long SizeBytes { get; set; }
    public Guid AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public EvidenceUpload Upload { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
}

/// <summary>
/// Who last signed in on this laptop. Lets the client open offline with the stored session (ADR-0005) and still
/// know whose edits it is recording. It holds no credential.
/// </summary>
public sealed class LocalProfileRow
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
}
