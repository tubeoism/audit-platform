using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Domain.Templates;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Engagements;

public sealed record TemplateImportResult(string AreaName, int Added, int Unchanged, IReadOnlyList<string> AddedCodes);

/// <summary>
/// The methodology library on the master: procedures imported from the firm's Markdown (repository
/// ia-aup-procedures), one row per code, language and version. A procedure whose text changed gets the next version;
/// earlier versions are kept, because a working paper pins the version it was created from.
/// </summary>
public sealed class TemplateService(MasterDbContext db, IAuditLog auditLog, TimeProvider clock)
{
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.Ordinal) { "en", "vi" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<ProcedureTemplateInfo>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await db.ProcedureTemplates.AsNoTracking()
                .Where(t => t.IsApproved)
                .OrderBy(t => t.Code).ThenBy(t => t.Language).ThenBy(t => t.Version)
                .ToListAsync(cancellationToken))
            .Select(t => new ProcedureTemplateInfo(t.Code, t.Version, t.Language, t.AreaCode, t.AreaName, t.Title, t.Procedure, t.AuditContent, t.EvidenceToCheck))];

    /// <summary>Imports one methodology file (one audit area). Importing the same text again changes nothing.</summary>
    public async Task<Outcome<TemplateImportResult>> ImportAsync(Actor actor, string markdown, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Languages.Contains(language))
        {
            return new AuthFailure("language_invalid", "Ngôn ngữ phải là en hoặc vi. / Language must be en or vi.");
        }

        var area = AupMarkdownParser.Parse(markdown ?? "", language);
        if (area.Procedures.Count == 0)
        {
            return new AuthFailure("template_empty", "Không tìm thấy thủ tục AUP-XXX-NN nào trong tệp. / No AUP-XXX-NN procedure found in the file.");
        }

        if (area.Procedures.FirstOrDefault(p => p.Code.Length > 20 || p.AreaCode.Length > 10) is { } tooLong)
        {
            return new AuthFailure("template_invalid", $"Mã thủ tục quá dài: {tooLong.Code}. / Procedure code is too long.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var codes = area.Procedures.Select(p => p.Code).ToList();
        var latest = (await db.ProcedureTemplates.AsNoTracking()
                .Where(t => t.Language == language && codes.Contains(t.Code))
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.Code)
            .ToDictionary(g => g.Key, g => g.MaxBy(t => t.Version)!);

        var now = clock.GetUtcNow();
        var added = new List<string>();
        foreach (var procedure in area.Procedures.DistinctBy(p => p.Code))
        {
            latest.TryGetValue(procedure.Code, out var current);
            if (current is not null && Same(current, procedure))
            {
                continue;
            }

            db.ProcedureTemplates.Add(new ProcedureTemplateRow
            {
                Code = procedure.Code, Version = (current?.Version ?? 0) + 1, Language = language, AreaCode = procedure.AreaCode,
                AreaName = procedure.AreaName, Title = procedure.Title, Procedure = procedure.Procedure,
                AuditContent = [.. procedure.AuditContent], EvidenceToCheck = [.. procedure.EvidenceToCheck],
                // The firm's methodology repository is the approval; the import records who brought it in and when.
                IsApproved = true, ApprovedBy = actor.UserId, ApprovedAt = now,
            });
            added.Add($"{procedure.Code} v{(current?.Version ?? 0) + 1}");
        }

        var result = new TemplateImportResult(area.AreaName, added.Count, area.Procedures.Count - added.Count, added);
        if (added.Count > 0)
        {
            await auditLog.AppendAsync(new AuditEvent(now, actor.UserId, "templates.imported", "procedure_templates", language, null,
                JsonSerializer.Serialize(new { via = actor.Via, address = actor.Address, detail = new { area = area.AreaName, language, added } }, Json)),
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private static bool Same(ProcedureTemplateRow row, ProcedureTemplate procedure) =>
        row.AreaCode == procedure.AreaCode && row.AreaName == procedure.AreaName && row.Title == procedure.Title
        && row.Procedure == procedure.Procedure
        && row.AuditContent.SequenceEqual(procedure.AuditContent, StringComparer.Ordinal)
        && row.EvidenceToCheck.SequenceEqual(procedure.EvidenceToCheck, StringComparer.Ordinal);
}
