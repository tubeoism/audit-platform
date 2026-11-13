using AuditPlatform.Client.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.LocalStore;

/// <summary>
/// One short unit of work on the local database: its own connection, replica and agent. The form and the
/// background sync loop each open their own, so neither ever shares a <see cref="LocalDbContext"/> across threads;
/// SQLite orders their writes.
/// </summary>
public sealed class LocalUnit(LocalDbContext db, ISyncTransport transport, ClientOptions options, TimeProvider clock) : IAsyncDisposable
{
    public LocalDbContext Db { get; } = db;
    public LocalReplica Replica { get; } = new(db);
    public SyncAgent Agent => field ??= new SyncAgent(Db, transport, Replica, options, clock);

    public EngagementWorkspace Workspace(Guid engagementId, Guid userId) =>
        new(Db, Agent, Replica, options, clock, engagementId, userId);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

public sealed class LocalUnitFactory(IDbContextFactory<LocalDbContext> contexts, ISyncTransport transport, ClientOptions options, TimeProvider clock)
{
    public async Task<LocalUnit> OpenAsync(CancellationToken cancellationToken) =>
        new(await contexts.CreateDbContextAsync(cancellationToken), transport, options, clock);
}
