using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Domain;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Engagements;

/// <summary>
/// Engagements and their members. Setting an engagement up and assigning people is account administration
/// (console or administrator); it gives the administrator no access to the engagement itself.
/// </summary>
public sealed class EngagementService(MasterDbContext db, IAuditLog auditLog, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The engagements this user is a member of. Nothing else is ever listed, to anyone.</summary>
    public async Task<List<EngagementInfo>> ListForAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Engagements.AsNoTracking()
            .Where(e => e.Members.Any(m => m.UserId == userId))
            .OrderBy(e => e.Code)
            .Select(e => new { Engagement = e, e.Members })
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(rows.SelectMany(r => r.Members).Select(m => m.UserId), cancellationToken);
        return [.. rows.Select(r => ToInfo(r.Engagement, r.Members, names))];
    }

    public async Task<List<EngagementInfo>> ListAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Engagements.AsNoTracking().Include(e => e.Members).OrderBy(e => e.Code).ToListAsync(cancellationToken);
        var names = await NamesAsync(rows.SelectMany(r => r.Members).Select(m => m.UserId), cancellationToken);
        return [.. rows.Select(r => ToInfo(r, r.Members, names))];
    }

    public async Task<Outcome<Guid>> CreateAsync(
        Actor actor, string code, string clientName, string contractReference, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        code = (code ?? "").Trim();
        Engagement engagement;
        try
        {
            // The domain object states the rules (required fields, period order, initial status).
            engagement = new Engagement(Guid.NewGuid(), code, (clientName ?? "").Trim(), (contractReference ?? "").Trim(), periodStart, periodEnd);
        }
        catch (Exception e) when (e is ArgumentException or DomainException)
        {
            return new AuthFailure("engagement_invalid", e.Message);
        }

        if (code.Length > 40 || await db.Engagements.AnyAsync(e => e.Code == code, cancellationToken))
        {
            return new AuthFailure("engagement_code_taken", $"Mã engagement '{code}' đã tồn tại hoặc quá dài. / Engagement code is taken or too long.");
        }

        db.Engagements.Add(new EngagementRow
        {
            Id = engagement.Id, Code = engagement.Code, ClientName = engagement.ClientName, ContractReference = engagement.ContractReference,
            PeriodStart = engagement.PeriodStart, PeriodEnd = engagement.PeriodEnd, Status = engagement.Status,
        });
        await WriteAsync(actor, "engagement.created", engagement.Id,
            new { code, clientName = engagement.ClientName, contractReference = engagement.ContractReference, periodStart, periodEnd }, cancellationToken);
        return engagement.Id;
    }

    /// <summary>Adds or removes one role of one user. A locked or archived engagement keeps its members as they were.</summary>
    public async Task<Outcome<bool>> SetMemberAsync(
        Actor actor, string code, Guid userId, EngagementRole role, bool member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // The same row lock the sync engine takes: a role change is ordered against every push of the engagement.
        var engagement = (await db.Engagements.FromSql($"SELECT * FROM engagements WHERE code = {code} FOR UPDATE")
            .ToListAsync(cancellationToken)).FirstOrDefault();
        if (engagement is null)
        {
            return new AuthFailure("engagement_unknown", $"Không tìm thấy engagement '{code}'. / Unknown engagement.");
        }

        if (engagement.Status is EngagementStatus.Locked or EngagementStatus.Archived)
        {
            return new AuthFailure("engagement_locked", "Engagement đã khoá; không đổi thành viên được. / The engagement is locked.");
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken))
        {
            return new AuthFailure("user_unknown", "Không tìm thấy tài khoản đang hoạt động. / No such active user.");
        }

        var existing = await db.EngagementMembers
            .FirstOrDefaultAsync(m => m.EngagementId == engagement.Id && m.UserId == userId && m.Role == role, cancellationToken);
        if (member == existing is not null)
        {
            return false;
        }

        if (member)
        {
            db.EngagementMembers.Add(new EngagementMemberRow { EngagementId = engagement.Id, UserId = userId, Role = role });
        }
        else
        {
            db.EngagementMembers.Remove(existing!);
        }

        await WriteAsync(actor, member ? "engagement.member.added" : "engagement.member.removed", engagement.Id,
            new { code, userId, role = role.ToString() }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private async Task WriteAsync(Actor actor, string action, Guid engagementId, object detail, CancellationToken cancellationToken)
    {
        await auditLog.AppendAsync(new AuditEvent(clock.GetUtcNow(), actor.UserId, action, "engagement", engagementId.ToString("D"), engagementId,
            JsonSerializer.Serialize(new { via = actor.Via, address = actor.Address, detail }, Json)), cancellationToken);

        // PostgresAuditLog has already saved pending changes with the entry; other implementations have not.
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }

    private static EngagementInfo ToInfo(EngagementRow e, IEnumerable<EngagementMemberRow> members, Dictionary<Guid, string> names) =>
        new(e.Id, e.Code, e.ClientName, e.ContractReference, e.PeriodStart, e.PeriodEnd, e.Status.ToString(),
            e.Status is EngagementStatus.Locked or EngagementStatus.Archived,
            [.. members.GroupBy(m => m.UserId)
                .Select(g => new EngagementMemberInfo(g.Key, names.GetValueOrDefault(g.Key, "?"), [.. g.Select(m => m.Role).Order().Select(r => r.ToString())]))
                .OrderBy(m => m.DisplayName, StringComparer.Ordinal).ThenBy(m => m.UserId)]);
}
