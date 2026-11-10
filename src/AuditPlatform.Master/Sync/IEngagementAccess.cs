using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Data;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Sync;

public sealed record EngagementAccess(bool Exists, bool IsMember, bool IsReadOnly);

public interface IEngagementAccess
{
    ValueTask<EngagementAccess> GetAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken);
}

internal sealed class EfEngagementAccess(MasterDbContext db) : IEngagementAccess
{
    public async ValueTask<EngagementAccess> GetAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken)
    {
        var row = await db.Engagements.AsNoTracking()
            .Where(e => e.Id == engagementId)
            .Select(e => new { e.Status, IsMember = e.Members.Any(m => m.UserId == userId) })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? new EngagementAccess(false, false, true)
            : new EngagementAccess(true, row.IsMember, row.Status is EngagementStatus.Locked or EngagementStatus.Archived);
    }
}
