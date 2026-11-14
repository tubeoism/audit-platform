using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Engagements;

/// <summary>
/// The phases of an engagement and its final lock. Only an engagement manager of the engagement acts here, and the
/// administrator gets no way round that. The lock is the one step that cannot be taken back: it needs the engagement
/// in Reporting, every working paper signed off at both review levels with nothing open, and a fresh authenticator
/// code bought for the exact state being locked (the same step-up as a sign-off).
/// </summary>
public sealed class EngagementLifecycle(MasterDbContext db, IAuditLog auditLog, ISignOffStepUp stepUp, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Outcome<EngagementStatus>> AdvanceAsync(Actor actor, Guid engagementId, EngagementStatus target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await LockRowAsync(engagementId, cancellationToken);
        if (Refuse(row, actor) is { } refusal)
        {
            return refusal;
        }

        var from = row!.Status;
        if (!Engagement.CanMove(from, target))
        {
            await WriteAsync(actor, "engagement.status.refused", row, new { from = from.ToString(), target = target.ToString() }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new AuthFailure("transition_refused", target is EngagementStatus.Locked or EngagementStatus.Archived
                ? "Khoá engagement là một thao tác riêng, cần mã xác thực. / Locking is a separate step and needs an authenticator code."
                : $"Không thể chuyển từ {from} sang {target}. / Cannot move from {from} to {target}.");
        }

        row.Status = target;
        await WriteAsync(actor, "engagement.status.changed", row, new { from = from.ToString(), to = target.ToString() }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return target;
    }

    /// <summary>What stands between the engagement and its lock. Read-only; any member may look.</summary>
    public async Task<LockReadiness?> ReadinessAsync(Guid engagementId, CancellationToken cancellationToken)
    {
        var row = await db.Engagements.AsNoTracking().FirstOrDefaultAsync(e => e.Id == engagementId, cancellationToken);
        return row is null ? null : await ComputeAsync(row, cancellationToken);
    }

    public async Task<Outcome<LockResponse>> LockAsync(Actor actor, Guid engagementId, string? stepUpToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // The row lock every writer of the engagement takes: a change is decided wholly before the lock or is refused after it.
        var row = await LockRowAsync(engagementId, cancellationToken);
        if (Refuse(row, actor) is { } refusal)
        {
            return refusal;
        }

        var readiness = await ComputeAsync(row!, cancellationToken);
        if (!readiness.Ready)
        {
            await WriteAsync(actor, "engagement.lock.refused", row!, new { reasons = readiness.Blockers.Select(b => b.Reason) }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new AuthFailure("lock_blocked", "Chưa đủ điều kiện khoá: " + string.Join(" ", readiness.Blockers.Select(b => b.Reason)));
        }

        // The engagement id stands in for the working paper and the digest for the content: the grant is for this state only.
        if (!await stepUp.ConsumeAsync(actor.UserId, stepUpToken, engagementId, readiness.Digest, cancellationToken))
        {
            await WriteAsync(actor, "engagement.lock.step_up_refused", row!, new { digest = readiness.Digest }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new AuthFailure("step_up_required",
                "Cần mã xác thực mới cho đúng trạng thái hiện tại của engagement. / A fresh authenticator code for the engagement as it stands now is required.");
        }

        var now = clock.GetUtcNow();
        row!.Status = EngagementStatus.Locked;
        row.LockedAt = now;
        row.LockedBy = actor.UserId;
        await WriteAsync(actor, "engagement.locked", row, new { digest = readiness.Digest, head = readiness.Head, papers = readiness.PaperCount }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new LockResponse(now, readiness.Digest);
    }

    /// <summary>
    /// Marks a locked engagement as archived once its package is safe in the immutable archive. This is a console step
    /// (an administrator on the VM), never an API call, and it only accepts a package the master itself built and
    /// recorded (<c>engagement.archive.packaged</c>): the digests must match that entry. The data does not change; an
    /// archived engagement is as read-only as a locked one, and it cannot be taken back.
    /// </summary>
    public async Task<Outcome<ArchiveResult>> ArchiveAsync(
        Actor actor, string engagementCode, string packageDigest, string tarSha256, string location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var code = (engagementCode ?? string.Empty).Trim();
        if (!IsSha256(packageDigest) || !IsSha256(tarSha256))
        {
            return new AuthFailure("digest_invalid", "Digest phải là 64 ký tự hex. / A digest is 64 hexadecimal characters.");
        }

        // One spelling in the audit log, whatever was typed.
        packageDigest = packageDigest.ToLowerInvariant();
        tarSha256 = tarSha256.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(location) || location.Length > 500)
        {
            return new AuthFailure("location_invalid", "Cần ghi rõ nơi lưu gói (tối đa 500 ký tự). / Say where the package is stored (at most 500 characters).");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = await db.Engagements.AsNoTracking().Where(e => e.Code == code).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(cancellationToken);
        var row = id is { } known ? await LockRowAsync(known, cancellationToken) : null;
        if (row is null)
        {
            return new AuthFailure("engagement_not_found", $"Không có engagement '{code}'. / No engagement '{code}'.");
        }

        if (row.Status != EngagementStatus.Locked)
        {
            return new AuthFailure(row.Status == EngagementStatus.Archived ? "already_archived" : "not_locked",
                row.Status == EngagementStatus.Archived
                    ? "Engagement đã lưu trữ rồi. / The engagement is already archived."
                    : $"Engagement đang ở {row.Status}; chỉ lưu trữ được sau khi khoá. / The engagement is {row.Status}; it can only be archived once locked.");
        }

        var packaged = (await db.AuditLog.AsNoTracking()
                .Where(a => a.EngagementId == row.Id && a.Action == "engagement.archive.packaged").OrderByDescending(a => a.Sequence).ToListAsync(cancellationToken))
            .FirstOrDefault(a => PackagedMatches(a.PayloadJson, packageDigest, tarSha256));
        if (packaged is null)
        {
            await WriteAsync(actor, "engagement.archive.refused", row, new { reason = "package_unknown", packageDigest, tarSha256 }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new AuthFailure("package_unknown",
                "Master chưa từng đóng gói với đúng hai digest này (chạy: audit-admin engagement package). / The master never built a package with these two digests.");
        }

        var now = clock.GetUtcNow();
        row.Status = EngagementStatus.Archived;
        await WriteAsync(actor, "engagement.archived", row, new { packageDigest, tarSha256, location = location.Trim(), packagedAtAuditSequence = packaged.Sequence }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ArchiveResult(now, packageDigest, location.Trim());
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool PackagedMatches(string payloadJson, string packageDigest, string tarSha256)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var detail = doc.RootElement.GetProperty("detail");
            return string.Equals(detail.GetProperty("packageDigest").GetString(), packageDigest, StringComparison.OrdinalIgnoreCase)
                && string.Equals(detail.GetProperty("tarSha256").GetString(), tarSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<EngagementRow?> LockRowAsync(Guid engagementId, CancellationToken cancellationToken) =>
        (await db.Engagements.FromSql($"SELECT * FROM engagements WHERE id = {engagementId} FOR UPDATE").Include(e => e.Members)
            .ToListAsync(cancellationToken)).FirstOrDefault();

    /// <summary>Same answer for "no such engagement" and "not a manager of it": nothing is revealed about engagements the caller is not in.</summary>
    private static AuthFailure? Refuse(EngagementRow? row, Actor actor)
    {
        if (row is null || !row.Members.Any(m => m.UserId == actor.UserId && m.Role == EngagementRole.EngagementManager))
        {
            return new AuthFailure("not_manager", "Chỉ engagement manager của engagement này mới thực hiện được. / Only an engagement manager of this engagement can do this.");
        }

        return row.Status is EngagementStatus.Locked or EngagementStatus.Archived
            ? new AuthFailure("engagement_locked", "Engagement đã khoá. / The engagement is locked.")
            : null;
    }

    private async Task<LockReadiness> ComputeAsync(EngagementRow engagement, CancellationToken cancellationToken)
    {
        var papers = await db.SyncDocuments.AsNoTracking().Where(d => d.EngagementId == engagement.Id).OrderBy(d => d.DocumentId).ToListAsync(cancellationToken);
        var head = await db.Changes.Where(c => c.EngagementId == engagement.Id).MaxAsync(c => (long?)c.Sequence, cancellationToken) ?? 0;
        var blockers = new List<LockBlocker>();
        if (engagement.Status != EngagementStatus.Reporting)
        {
            blockers.Add(new LockBlocker(null, null,
                $"Engagement đang ở {engagement.Status}; chỉ khoá được khi ở Reporting. / The engagement is {engagement.Status}; it can only be locked in Reporting."));
        }

        if (papers.Count == 0)
        {
            blockers.Add(new LockBlocker(null, null, "Chưa có giấy làm việc nào. / There are no working papers."));
        }

        foreach (var paper in papers)
        {
            var done = nameof(WorkingPaperStatus.JapanDeskSignedOff);
            if (paper.Status != done)
            {
                blockers.Add(new LockBlocker(paper.DocumentId, paper.TemplateCode,
                    $"{paper.TemplateCode} đang ở {paper.Status}, chưa qua đủ hai cấp soát xét. / {paper.TemplateCode} is {paper.Status}, not signed off at both review levels."));
            }

            if (paper.OpenConflicts > 0)
            {
                blockers.Add(new LockBlocker(paper.DocumentId, paper.TemplateCode,
                    $"{paper.TemplateCode} còn {paper.OpenConflicts} xung đột chưa giải quyết. / {paper.TemplateCode} has {paper.OpenConflicts} unresolved conflict(s)."));
            }

            var open = FieldDocument.FromJson(paper.State).Notes.Values.Count(n => !n.IsCleared);
            if (open > 0)
            {
                blockers.Add(new LockBlocker(paper.DocumentId, paper.TemplateCode,
                    $"{paper.TemplateCode} còn {open} ghi chú soát xét chưa đóng. / {paper.TemplateCode} has {open} open review note(s)."));
            }
        }

        return new LockReadiness(engagement.Id, engagement.Status.ToString(), blockers.Count == 0, papers.Count,
            Digest(engagement.Id, head, papers), head, blockers);
    }

    /// <summary>
    /// A name for the engagement's content: the head of its change log and each working paper's content hash. Any
    /// change to any paper, note, conflict or sign-off moves the head, so a grant bought for one digest cannot lock another state.
    /// </summary>
    internal static string Digest(Guid engagementId, long head, IEnumerable<SyncDocumentRow> papers)
    {
        var text = new StringBuilder().Append(engagementId.ToString("D")).Append('|').Append(head);
        foreach (var paper in papers.OrderBy(p => p.DocumentId))
        {
            text.Append('|').Append(paper.DocumentId.ToString("D")).Append(':').Append(paper.ContentHash);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private async Task WriteAsync(Actor actor, string action, EngagementRow engagement, object detail, CancellationToken cancellationToken)
    {
        await auditLog.AppendAsync(new AuditEvent(clock.GetUtcNow(), actor.UserId, action, "engagement", engagement.Id.ToString("D"), engagement.Id,
            JsonSerializer.Serialize(new { via = actor.Via, address = actor.Address, code = engagement.Code, detail }, Json)), cancellationToken);

        // PostgresAuditLog has already saved pending changes with the entry; other implementations have not.
        await db.SaveChangesAsync(cancellationToken);
    }
}
