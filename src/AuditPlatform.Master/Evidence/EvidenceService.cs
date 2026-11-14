using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Evidence;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Evidence;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Evidence;

public enum EvidenceOutcome
{
    Ok = 0,
    NotMember = 1,
    NotAllowed = 2,
    Locked = 3,
    HashMismatch = 4,
    NotFound = 5,
    Damaged = 6,

    /// <summary>The request itself is wrong (a part number or size that does not fit the file).</summary>
    BadRequest = 7,

    /// <summary>A parted upload is completed before every part has arrived.</summary>
    Incomplete = 8,
}

public sealed record EvidenceResult(EvidenceOutcome Outcome, string? Reason = null, EvidenceInfo? Info = null);

/// <param name="Checked">Objects the database lists and the store was asked for.</param>
/// <param name="Bytes">Size of the objects that were read and matched their hash.</param>
/// <param name="Missing">Hashes the database lists and the store does not hold.</param>
/// <param name="Damaged">Hashes whose stored bytes no longer hash to their address.</param>
public sealed record EvidenceVerification(int Checked, long Bytes, IReadOnlyList<string> Missing, IReadOnlyList<string> Damaged)
{
    public bool IsClean => Missing.Count == 0 && Damaged.Count == 0;
}

public sealed record EvidenceDownload(EvidenceOutcome Outcome, Stream? Content = null, EvidenceInfo? Info = null, string? Reason = null);

/// <summary>
/// Evidence files of an engagement (ADR-0003): the bytes go to the content-addressed store on the block volume, the
/// database keeps the hash and one intake row per receipt, and every receipt and every read is written to the audit
/// log. A file belongs to the engagement it was received for: knowing a hash does not open it from another one.
/// </summary>
public sealed class EvidenceService(MasterDbContext db, IEvidenceStore store, IEngagementAccess access, IAuditLog auditLog, TimeProvider clock)
{
    public const string Source = "client-upload";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Receives one file from a member who may edit working papers. The content must hash to <paramref name="hash"/>.
    /// Safe to repeat: the same person sending the same file under the same name again adds nothing.
    /// </summary>
    public async Task<EvidenceResult> ReceiveAsync(
        Guid engagementId, Guid userId, string hash, string fileName, string? mediaType, Stream content, CancellationToken cancellationToken)
    {
        if (await CheckCanUploadAsync(engagementId, userId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        EvidenceRef stored;
        try
        {
            stored = await store.PutAsync(content, hash, cancellationToken);
        }
        catch (EvidenceIntegrityException e)
        {
            return new EvidenceResult(EvidenceOutcome.HashMismatch, e.Message);
        }

        var ticks = clock.GetUtcNow().UtcTicks;
        var now = new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // The same row lock as a push: a file is received either wholly before the engagement is locked or not at all.
        var status = await db.Database
            .SqlQuery<string>($"""SELECT status AS "Value" FROM engagements WHERE id = {engagementId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        if (status is not [var current] || current is nameof(EngagementStatus.Locked) or nameof(EngagementStatus.Archived))
        {
            return new EvidenceResult(EvidenceOutcome.Locked, FieldSyncEngine.LockedReason);
        }

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO evidence_objects (hash, size_bytes, first_received_at) VALUES ({hash}, {stored.SizeBytes}, {now}) ON CONFLICT (hash) DO NOTHING",
            cancellationToken);

        var again = await db.EvidenceIntake.AsNoTracking().AnyAsync(
            i => i.EngagementId == engagementId && i.Hash == hash && i.ReceivedBy == userId && i.OriginalFileName == fileName, cancellationToken);
        if (!again)
        {
            db.EvidenceIntake.Add(new EvidenceIntakeRow
            {
                Id = Guid.NewGuid(), Hash = hash, EngagementId = engagementId, OriginalFileName = fileName, MediaType = mediaType,
                ReceivedBy = userId, ReceivedAt = now, Source = Source,
            });
            await auditLog.AppendAsync(new AuditEvent(now, userId, "evidence.received", "evidence", hash, engagementId,
                JsonSerializer.Serialize(new { fileName, mediaType, sizeBytes = stored.SizeBytes, source = Source }, Json)), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new EvidenceResult(EvidenceOutcome.Ok, Info: (await FirstIntakeAsync(engagementId, hash, cancellationToken))!);
    }

    /// <summary>Null when this member may add evidence to the engagement now; otherwise why not.</summary>
    public async Task<EvidenceResult?> CheckCanUploadAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken)
    {
        var grant = await access.GetAsync(engagementId, userId, cancellationToken);
        if (!grant.Exists || !grant.IsMember)
        {
            return new EvidenceResult(EvidenceOutcome.NotMember, "Not a member of this engagement.");
        }

        if (grant.IsReadOnly)
        {
            return new EvidenceResult(EvidenceOutcome.Locked, FieldSyncEngine.LockedReason);
        }

        return FieldPolicy.CanEdit(grant.Roles) ? null : new EvidenceResult(EvidenceOutcome.NotAllowed, FieldPolicy.NotAnEditor);
    }

    /// <summary>Every evidence file of the engagement, one entry per content, described by its first intake.</summary>
    public async Task<IReadOnlyList<EvidenceInfo>?> ListAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken)
    {
        var grant = await access.GetAsync(engagementId, userId, cancellationToken);
        if (!grant.Exists || !grant.IsMember)
        {
            return null;
        }

        var rows = await Intake(engagementId).ToListAsync(cancellationToken);
        return [.. rows.GroupBy(r => r.Hash).Select(g => g.First()).OrderBy(r => r.ReceivedAt).ThenBy(r => r.Hash, StringComparer.Ordinal)];
    }

    /// <summary>Opens a file of the engagement for a member, after checking that the stored bytes still match the hash.</summary>
    public async Task<EvidenceDownload> OpenAsync(Guid engagementId, Guid userId, string hash, CancellationToken cancellationToken)
    {
        var grant = await access.GetAsync(engagementId, userId, cancellationToken);
        if (!grant.Exists || !grant.IsMember)
        {
            return new EvidenceDownload(EvidenceOutcome.NotMember);
        }

        if (!EvidenceHash.IsValid(hash) || await FirstIntakeAsync(engagementId, hash, cancellationToken) is not { } info)
        {
            return new EvidenceDownload(EvidenceOutcome.NotFound);
        }

        var now = clock.GetUtcNow();
        try
        {
            var content = await store.OpenVerifiedAsync(hash, cancellationToken);
            await auditLog.AppendAsync(new AuditEvent(now, userId, "evidence.read", "evidence", hash, engagementId, "{}"), cancellationToken);
            return new EvidenceDownload(EvidenceOutcome.Ok, content, info);
        }
        catch (Exception e) when (e is EvidenceIntegrityException or FileNotFoundException or DirectoryNotFoundException)
        {
            // The record says the master holds this file and the volume disagrees: that is an incident, not a 404.
            await auditLog.AppendAsync(new AuditEvent(now, userId, "evidence.damaged", "evidence", hash, engagementId,
                JsonSerializer.Serialize(new { reason = e.Message }, Json)), cancellationToken);
            return new EvidenceDownload(EvidenceOutcome.Damaged, Reason: "The stored file is missing or no longer matches its hash.");
        }
    }

    /// <summary>
    /// Re-hashes every object the database lists and reports what is missing or altered. Run after a restore of the
    /// volume or of the mirror, and now and then on the live store; the result is written to the audit log.
    /// </summary>
    public async Task<EvidenceVerification> VerifyAllAsync(string operatorName, CancellationToken cancellationToken)
    {
        var listed = await db.EvidenceObjects.AsNoTracking().OrderBy(o => o.Hash).Select(o => new { o.Hash, o.SizeBytes }).ToListAsync(cancellationToken);
        List<string> missing = [], damaged = [];
        long bytes = 0;
        foreach (var item in listed)
        {
            try
            {
                await using var stream = await store.OpenVerifiedAsync(item.Hash, cancellationToken);
                bytes += stream.Length;
            }
            catch (EvidenceIntegrityException)
            {
                damaged.Add(item.Hash);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Add(item.Hash);
            }
        }

        var result = new EvidenceVerification(listed.Count, bytes, missing, damaged);
        await auditLog.AppendAsync(new AuditEvent(clock.GetUtcNow(), Guid.Empty, "evidence.verified", "evidence", "store", null,
            JsonSerializer.Serialize(new { operatorName, checkedObjects = result.Checked, bytes, missing, damaged, clean = result.IsClean }, Json)), cancellationToken);
        return result;
    }

    private async Task<EvidenceInfo?> FirstIntakeAsync(Guid engagementId, string hash, CancellationToken cancellationToken) =>
        await Intake(engagementId, hash).FirstOrDefaultAsync(cancellationToken);

    private IQueryable<EvidenceInfo> Intake(Guid engagementId, string? hash = null) =>
        from i in db.EvidenceIntake.AsNoTracking()
        join o in db.EvidenceObjects.AsNoTracking() on i.Hash equals o.Hash
        where i.EngagementId == engagementId && (hash == null || i.Hash == hash)
        orderby i.ReceivedAt, i.Id
        select new EvidenceInfo(i.Hash, o.SizeBytes, i.OriginalFileName, i.MediaType, i.ReceivedBy, i.ReceivedAt);
}
