using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Data;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Sync;

public sealed record EngagementAccess(bool Exists, bool IsMember, bool IsReadOnly)
{
    /// <summary>The caller's roles in the engagement. A review level can be signed only by a member holding that role.</summary>
    public IReadOnlySet<EngagementRole> Roles { get; init; } = new HashSet<EngagementRole>();
}

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
            .Select(e => new { e.Status, Roles = e.Members.Where(m => m.UserId == userId).Select(m => m.Role).ToList() })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? new EngagementAccess(false, false, true)
            : new EngagementAccess(true, row.Roles.Count > 0, row.Status is EngagementStatus.Locked or EngagementStatus.Archived)
            {
                Roles = row.Roles.ToHashSet(),
            };
    }
}
