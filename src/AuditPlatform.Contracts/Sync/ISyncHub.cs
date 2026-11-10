namespace AuditPlatform.Contracts.Sync;

/// <summary>Methods a client calls on the master's SignalR hub.</summary>
public interface ISyncHub
{
    /// <summary>Joins the real-time group for an engagement the caller is a member of.</summary>
    Task Subscribe(Guid engagementId);

    Task Unsubscribe(Guid engagementId);

    /// <summary>Pushes local changes. The master checks membership and lock before passing them to the sync engine.</summary>
    Task<PushResult> Push(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes);

    /// <summary>Pulls changes after <paramref name="since"/>, in master order.</summary>
    Task<PullResult> Pull(Guid engagementId, SyncCursor since, int maxChanges);
}

/// <summary>Callbacks the master sends to connected clients.</summary>
public interface ISyncClient
{
    /// <summary>New changes exist for the engagement up to <paramref name="cursor"/>; the client should pull.</summary>
    Task ChangesAvailable(Guid engagementId, SyncCursor cursor);

    /// <summary>The engagement has been locked; the client must switch it to read-only.</summary>
    Task EngagementLocked(Guid engagementId, DateTimeOffset lockedAt);
}

public static class SyncHubRoutes
{
    public const string Path = "/hubs/sync";
}
