using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Client.Core.Sync;

public enum ConnectionState
{
    Offline = 0,
    Connecting = 1,
    Online = 2,
}

/// <summary>Client side of the sync channel. Abstracted so the agent can be tested without a master.</summary>
public interface ISyncTransport
{
    ConnectionState State { get; }

    event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Raised when the master signals new changes for a subscribed engagement.</summary>
    event EventHandler<Guid>? ChangesAvailable;

    event EventHandler<Guid>? EngagementLocked;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task SubscribeAsync(Guid engagementId, CancellationToken cancellationToken);

    Task<PushResult> PushAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken);

    Task<PullResult> PullAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken);
}
