using AuditPlatform.AuditLog;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Data;

/// <summary>
/// Appends to <c>audit_log</c> under a transaction-scoped advisory lock so concurrent writers chain correctly.
/// The table itself rejects UPDATE, DELETE and TRUNCATE (see migration InitialSchema).
/// Pending changes tracked by the same context are saved in the same transaction as the entry.
/// </summary>
public sealed class PostgresAuditLog(MasterDbContext db) : IAuditLog
{
    // Arbitrary constant identifying the audit-log append lock.
    private const long AppendLockKey = 0x4155_4449_544C_4F47; // "AUDITLOG"

    public async ValueTask<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return (await AppendManyAsync([auditEvent], cancellationToken))[0];
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> AppendManyAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);
        if (auditEvents.Count == 0)
        {
            return [];
        }

        // Joins the caller's transaction when there is one, so a state change and its audit entries commit together.
        var ambient = db.Database.CurrentTransaction is not null;
        await using var tx = ambient ? null : await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AppendLockKey})", cancellationToken);

        var last = await db.AuditLog.AsNoTracking()
            .OrderByDescending(x => x.Sequence)
            .Select(x => new { x.Sequence, x.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = last?.Sequence ?? 0;
        var previous = last?.Hash ?? HashChain.GenesisHash;
        var entries = new List<AuditEntry>(auditEvents.Count);
        foreach (var auditEvent in auditEvents)
        {
            var entry = HashChain.Seal(++sequence, previous, auditEvent);
            previous = entry.Hash;
            entries.Add(entry);
            db.AuditLog.Add(new AuditLogRow
            {
                Sequence = entry.Sequence,
                OccurredAt = entry.OccurredAt,
                ActorUserId = entry.ActorUserId,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                EngagementId = entry.EngagementId,
                PayloadJson = entry.PayloadJson,
                PreviousHash = entry.PreviousHash,
                Hash = entry.Hash,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        if (tx is not null)
        {
            await tx.CommitAsync(cancellationToken);
        }

        return entries;
    }
}
