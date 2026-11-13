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
}
