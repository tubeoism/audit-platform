using AuditPlatform.AuditLog;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Data;

/// <summary>
/// Appends to <c>audit_log</c> under a transaction-scoped advisory lock so concurrent writers chain correctly.
/// The table itself rejects UPDATE, DELETE and TRUNCATE (see migration InitialSchema).
/// </summary>
public sealed class PostgresAuditLog(MasterDbContext db) : IAuditLog
{
    // Arbitrary constant identifying the audit-log append lock.
    private const long AppendLockKey = 0x4155_4449_544C_4F47; // "AUDITLOG"

    public async ValueTask<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AppendLockKey})", cancellationToken);

        var last = await db.AuditLog.AsNoTracking()
            .OrderByDescending(x => x.Sequence)
            .Select(x => new { x.Sequence, x.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var entry = HashChain.Seal((last?.Sequence ?? 0) + 1, last?.Hash ?? HashChain.GenesisHash, auditEvent);
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
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return entry;
    }
}
