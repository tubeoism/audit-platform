using AuditPlatform.Client.Core.LocalStore;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>
/// Rebuilds the local replica of one engagement from the master. The replica holds only what the master has decided
/// (the pulled log and the papers folded from it), so it can always be thrown away and pulled again; the author's
/// unsent edits live in the outbox and are kept. Before anything is deleted the whole local database is copied aside,
/// so that nothing this laptop holds is lost even if the master turns out to be the one that is behind.
/// </summary>
public static class ReplicaRebuild
{
    /// <summary>Returns the path of the copy made before the rebuild.</summary>
    public static async Task<string> RunAsync(
        LocalDbContext db, ClientOptions options, DateTimeOffset now, Guid engagementId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var code = await db.Engagements.AsNoTracking().Where(e => e.Id == engagementId).Select(e => e.Code).FirstOrDefaultAsync(cancellationToken)
            ?? engagementId.ToString("N");
        var folder = Path.Combine(options.DataDirectory, "backups");
        System.IO.Directory.CreateDirectory(folder);
        var safe = new string([.. code.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        var copy = Path.Combine(folder, $"replica-{safe}-{now.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..6]}.db");

        // A consistent copy of the live database, including what is still in the write-ahead log.
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {copy}", cancellationToken);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ReplicaLog.Where(r => r.EngagementId == engagementId).ExecuteDeleteAsync(cancellationToken);
        await db.Documents.Where(d => d.EngagementId == engagementId).ExecuteDeleteAsync(cancellationToken);
        await db.SyncState.Where(s => s.EngagementId == engagementId).ExecuteDeleteAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return copy;
    }
}
