using System.Text.Json;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.Templates;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Directory;

/// <summary>What the master lists for the signed-in user. Either part is null when it could not be read.</summary>
public sealed record DirectorySnapshot(IReadOnlyList<EngagementInfo>? Engagements, IReadOnlyList<ProcedureTemplateInfo>? Templates);

/// <summary>Reads the directory from the master. Abstracted so the sync loop can be tested without one.</summary>
public interface IDirectorySource
{
    Task<DirectorySnapshot> FetchAsync(CancellationToken cancellationToken);
}

public sealed class MasterDirectorySource(AuthSession session) : IDirectorySource
{
    public async Task<DirectorySnapshot> FetchAsync(CancellationToken cancellationToken) =>
        new(await session.GetAsync<List<EngagementInfo>>(DirectoryRoutes.Engagements, cancellationToken),
            await session.GetAsync<List<ProcedureTemplateInfo>>(DirectoryRoutes.Templates, cancellationToken));
}

/// <summary>
/// The local copy of the directory: the user's engagements with their members, the methodology library, and who is
/// signed in. It is a cache of what the master said last; working papers themselves arrive through the sync log.
/// </summary>
public static class DirectoryStore
{
    public static async Task SaveProfileAsync(LocalDbContext db, UserInfo user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(user);
        var row = await db.Profile.FirstOrDefaultAsync(p => p.Id == 1, cancellationToken);
        if (row is null)
        {
            db.Profile.Add(new LocalProfileRow { Id = 1, UserId = user.Id, Email = user.Email, DisplayName = user.DisplayName });
        }
        else
        {
            (row.UserId, row.Email, row.DisplayName) = (user.Id, user.Email, user.DisplayName);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static Task<LocalProfileRow?> LoadProfileAsync(LocalDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Profile.AsNoTracking().FirstOrDefaultAsync(p => p.Id == 1, cancellationToken);
    }

    /// <summary>
    /// Stores what the master listed. An engagement the user no longer belongs to leaves the list; its replica and
    /// any unsent work stay in the database.
    /// </summary>
    public static async Task ApplyAsync(LocalDbContext db, DirectorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Engagements is { } engagements)
        {
            var existing = await db.Engagements.ToDictionaryAsync(e => e.Id, cancellationToken);
            foreach (var info in engagements)
            {
                if (!existing.Remove(info.Id, out var row))
                {
                    row = new LocalEngagementRow { Id = info.Id, Code = info.Code, ClientName = info.ClientName };
                    db.Engagements.Add(row);
                }

                row.Code = info.Code;
                row.ClientName = info.ClientName;
                row.ContractReference = info.ContractReference;
                row.PeriodStart = info.PeriodStart;
                row.PeriodEnd = info.PeriodEnd;
                row.Status = Enum.TryParse<EngagementStatus>(info.Status, out var status) ? status : row.Status;
                row.IsReadOnly = info.IsReadOnly;
            }

            db.Engagements.RemoveRange(existing.Values);
            db.Members.RemoveRange(await db.Members.ToListAsync(cancellationToken));
            db.Members.AddRange(engagements.SelectMany(e => e.Members.Select(m => new LocalMemberRow
            {
                EngagementId = e.Id, UserId = m.UserId, DisplayName = m.DisplayName, Roles = string.Join(',', m.Roles),
            })));
        }

        if (snapshot.Templates is { } templates)
        {
            var known = (await db.Templates.AsNoTracking().Select(t => new { t.Code, t.Version, t.Language }).ToListAsync(cancellationToken))
                .Select(t => (t.Code, t.Version, t.Language)).ToHashSet();

            // Versions are immutable on the master, so only new ones are written.
            db.Templates.AddRange(templates.Where(t => known.Add((t.Code, t.Version, t.Language))).Select(t => new LocalTemplateRow
            {
                Code = t.Code, Version = t.Version, Language = t.Language, AreaCode = t.AreaCode, AreaName = t.AreaName, Title = t.Title,
                Procedure = t.Procedure, AuditContent = JsonSerializer.Serialize(t.AuditContent), EvidenceToCheck = JsonSerializer.Serialize(t.EvidenceToCheck),
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The newest version of every procedure in the preferred language, for starting a working paper.</summary>
    public static async Task<List<ProcedureTemplate>> LatestTemplatesAsync(LocalDbContext db, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Templates.AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.GroupBy(r => r.Code)
            .Select(g => g.OrderByDescending(r => r.Version).ThenByDescending(r => r.Language == language).First())
            .Select(latest => rows.FirstOrDefault(r => r.Code == latest.Code && r.Version == latest.Version && r.Language == language) ?? latest)
            .OrderBy(r => r.AreaCode, StringComparer.Ordinal).ThenBy(r => r.Code, StringComparer.Ordinal)
            .Select(ToTemplate)];
    }

    /// <summary>
    /// The wording a working paper was created from: its pinned version in the preferred language, otherwise the
    /// same version in the other language. Null when this laptop has never received that version.
    /// </summary>
    public static async Task<ProcedureTemplate?> FindTemplateAsync(LocalDbContext db, string code, int version, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Templates.AsNoTracking().Where(t => t.Code == code && t.Version == version).ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault(r => r.Language == language) ?? rows.FirstOrDefault();
        return row is null ? null : ToTemplate(row);
    }

    private static ProcedureTemplate ToTemplate(LocalTemplateRow r) =>
        new(r.Code, r.AreaCode, r.AreaName, r.Title, r.Procedure,
            JsonSerializer.Deserialize<List<string>>(r.AuditContent) ?? [], JsonSerializer.Deserialize<List<string>>(r.EvidenceToCheck) ?? [],
            r.Language, r.Version);
}
