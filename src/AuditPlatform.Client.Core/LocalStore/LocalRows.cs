using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Client.Core.LocalStore;

public enum OutboxState
{
    Pending = 0,
    Conflict = 1,
    Rejected = 2,
}

/// <summary>A local change waiting to reach the master. Survives restarts; removed once the master accepts it.</summary>
public sealed class OutboxChangeRow
{
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

/// <summary>Replica header for an engagement assigned to this user.</summary>
public sealed class LocalEngagementRow
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string ClientName { get; set; }
    public EngagementStatus Status { get; set; }
    public bool IsReadOnly { get; set; }
}
