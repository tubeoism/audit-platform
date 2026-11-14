using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Evidence;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Engagements;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Archive;

/// <summary>What a finished package is: enough to name it, to find it again and to check a copy of it.</summary>
/// <param name="TarSha256">SHA-256 of the tar file; a copy in the archive bucket must hash to the same value.</param>
/// <param name="PackageDigest">SHA-256 of the checksum list inside the tar; one value for the whole content.</param>
/// <param name="LockDigest">The digest the manager's authenticator code was bought for when the engagement was locked.</param>
public sealed record ArchivePackageInfo(
    Guid EngagementId, string Code, string FileName, long Bytes, string TarSha256, string PackageDigest, string LockDigest,
    int Papers, int Changes, int AuditEntries, int EvidenceFiles, long EvidenceBytes);

/// <summary>
/// Builds the package of a locked engagement that goes to the immutable archive (docs/engagement-archive.md): the
/// engagement, its working papers, the full change log, the audit-log entries about it and every evidence file, in one
/// tar with a checksum list. It reads only; it changes nothing about the engagement. Before it writes a byte it checks
/// that what the database holds is still what was locked, and after it has written the file it reads the file back and
/// checks every byte against the list, so a package that exists is a package that verifies.
/// </summary>
public sealed class ArchivePackager(MasterDbContext db, IEvidenceStore store, IAuditLog auditLog, TimeProvider clock)
{
    public const int FormatVersion = 1;
    private const int MaxReportedProblems = 20;

    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<Outcome<ArchivePackageInfo>> BuildAsync(Actor actor, string engagementCode, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var code = (engagementCode ?? string.Empty).Trim();
        var engagement = await db.Engagements.AsNoTracking().Include(e => e.Members).FirstOrDefaultAsync(e => e.Code == code, cancellationToken);
        if (engagement is null)
        {
            return new AuthFailure("engagement_not_found", $"Không có engagement '{code}'. / No engagement '{code}'.");
        }

        if (engagement.Status is not (EngagementStatus.Locked or EngagementStatus.Archived))
        {
            return new AuthFailure("not_locked",
                $"Engagement đang ở {engagement.Status}; chỉ đóng gói được sau khi khoá. / The engagement is {engagement.Status}; it can only be packaged once locked.");
        }

        var path = Path.GetFullPath(outputPath);
        var part = path + ".part";
        if (File.Exists(path) || File.Exists(part))
        {
            return new AuthFailure("output_exists", $"Đã có tệp {path}(.part); không ghi đè. / {path}(.part) exists; nothing is overwritten.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return new AuthFailure("output_directory_missing", $"Không có thư mục {Path.GetDirectoryName(path)}. / The folder does not exist.");
        }

        var id = engagement.Id;
        var papers = await db.SyncDocuments.AsNoTracking().Where(d => d.EngagementId == id).OrderBy(d => d.DocumentId).ToListAsync(cancellationToken);
        var changes = await db.Changes.AsNoTracking().Where(c => c.EngagementId == id).OrderBy(c => c.Sequence).ToListAsync(cancellationToken);
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EngagementId == id).OrderBy(a => a.Sequence).ToListAsync(cancellationToken);
        var intake = await db.EvidenceIntake.AsNoTracking().Where(i => i.EngagementId == id).OrderBy(i => i.ReceivedAt).ThenBy(i => i.Id).ToListAsync(cancellationToken);
        var hashes = intake.Select(i => i.Hash).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var objects = await db.EvidenceObjects.AsNoTracking().Where(o => hashes.Contains(o.Hash)).ToDictionaryAsync(o => o.Hash, StringComparer.Ordinal, cancellationToken);
        var head = changes.Count == 0 ? 0 : changes[^1].Sequence;
        var chainHead = await db.AuditLog.AsNoTracking().OrderByDescending(a => a.Sequence).Select(a => new { a.Sequence, a.Hash }).FirstOrDefaultAsync(cancellationToken);

        var lockDigest = EngagementLifecycle.Digest(id, head, papers);
        var problems = await CheckAsync(engagement, papers, audit, hashes, objects, lockDigest, cancellationToken);
        if (problems.Count > 0)
        {
            await WriteAuditAsync(actor, "engagement.archive.refused", engagement, new { problems = problems.Take(MaxReportedProblems), total = problems.Count }, cancellationToken);
            return new AuthFailure("archive_blocked", "Không đóng gói được: " + string.Join(" ", problems.Take(MaxReportedProblems)));
        }

        var userIds = engagement.Members.Select(m => m.UserId)
            .Concat(intake.Select(i => i.ReceivedBy)).Concat(papers.Select(p => p.PreparerId))
            .Concat(engagement.LockedBy is { } by ? [by] : []).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var evidenceBytes = hashes.Sum(h => objects[h].SizeBytes);
        var now = clock.GetUtcNow();
        var root = $"engagement-{id:D}";

        var entries = new List<ArchiveEntry>
        {
            Json("manifest.json", Manifest(engagement, now, lockDigest, head, chainHead?.Sequence ?? 0, chainHead?.Hash ?? HashChain.GenesisHash,
                papers.Count, changes.Count, audit.Count, hashes.Count, evidenceBytes)),
            ArchiveEntry.FromBytes("README.txt", Encoding.UTF8.GetBytes(Readme(engagement))),
            Json("engagement.json", EngagementDocument(engagement, users)),
            ArchiveEntry.FromBytes("changes.jsonl", Lines(changes.Select(c => new
            {
                c.Sequence, c.ChangeId, c.DocumentId, c.AuthorUserId, c.ClientId, c.ClientCreatedAt, c.ReceivedAt, c.PayloadKind,
                PayloadBase64 = Convert.ToBase64String(c.Payload),
            }))),
            ArchiveEntry.FromBytes("audit-log.jsonl", Lines(audit.Select(a => new
            {
                a.Sequence, a.OccurredAt, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.EngagementId, a.PayloadJson, a.PreviousHash, a.Hash,
            }))),
            Json("evidence/index.json", new
            {
                // The name of each file is the BLAKE3-256 of its content, the address every record in the system uses.
                // SHA256SUMS holds the SHA-256, which any machine can check.
                files = hashes.Select(h => new { blake3 = h, sizeBytes = objects[h].SizeBytes, path = $"evidence/files/{h}" }),
                receipts = intake.Select(i => new
                {
                    i.Hash, i.OriginalFileName, i.MediaType, i.ReceivedAt, i.ReceivedBy, receivedByName = users.GetValueOrDefault(i.ReceivedBy)?.DisplayName, i.Source,
                }),
            }),
        };
        foreach (var paper in papers)
        {
            using var state = JsonDocument.Parse(paper.State);
            entries.Add(Json($"papers/{paper.DocumentId:D}.json", new
            {
                paper.DocumentId, paper.TemplateCode, paper.Status, paper.ContentHash, paper.OpenConflicts, paper.LastSequence, paper.UpdatedAt,
                paper.PreparerId, preparerName = users.GetValueOrDefault(paper.PreparerId)?.DisplayName, state = state.RootElement.Clone(),
            }));
        }

        foreach (var hash in hashes)
        {
            var captured = hash;
            entries.Add(ArchiveEntry.FromStream($"evidence/files/{hash}", ct => store.OpenVerifiedAsync(captured, ct)));
        }

        ArchiveTarResult written;
        ArchiveVerification checkedFile;
        var finished = false;
        try
        {
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                written = await ArchiveTar.WriteAsync(file, root, entries, engagement.LockedAt ?? now, cancellationToken);

                // Durability: the package is about to be vouched for and renamed into place. The read-back below goes through the
                // operating system's cache like any read; the check against the bucket is what proves the disk-to-R2 path.
                file.Flush(flushToDisk: true);
                file.Position = 0;
                checkedFile = await ArchiveTar.VerifyAsync(file, cancellationToken);
            }

            File.Move(part, path);
            finished = true;
        }
        // What the write and the read-back can really throw: a failed check, a store that no longer matches its hash, the file
        // system, a damaged tar. A programming error is not turned into a "failed package": it surfaces with its stack trace.
        catch (Exception e) when (e is ArchiveIntegrityException or EvidenceIntegrityException or IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            await WriteAuditAsync(actor, "engagement.archive.refused", engagement, new { failed = e.Message }, cancellationToken);
            return new AuthFailure("archive_failed", "Ghi gói lưu trữ không thành công: " + e.Message);
        }
        finally
        {
            // Whatever stopped the write (a failed check, a cancellation, a full disk), no half-written package stays
            // behind to block the next run.
            if (!finished)
            {
                TryDelete(part);
            }
        }

        var info = new ArchivePackageInfo(id, engagement.Code, Path.GetFileName(path), checkedFile.Bytes, checkedFile.TarSha256, written.PackageDigest, lockDigest,
            papers.Count, changes.Count, audit.Count, hashes.Count, evidenceBytes);
        try
        {
            await WriteAuditAsync(actor, "engagement.archive.packaged", engagement, info, cancellationToken);
        }
        catch
        {
            // A package without its record cannot be archived, and a file left behind would block the next run:
            // the package can be built again, so remove it. The error that matters is the audit one, not a failed delete.
            TryDelete(path);
            throw;
        }

        return info;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the caller is already handling a failure, and this must not replace it.
        }
    }

    /// <summary>Everything that must be true before a package is worth sealing. All problems are reported at once.</summary>
    private async Task<List<string>> CheckAsync(
        EngagementRow engagement, List<SyncDocumentRow> papers, List<AuditLogRow> audit, List<string> hashes,
        Dictionary<string, EvidenceObjectRow> objects, string lockDigest, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        // What is in the database must still be what the manager locked: the digest of the lock event is recomputed.
        if (audit.LastOrDefault(a => a.Action == "engagement.locked") is not { } locked)
        {
            problems.Add("Không thấy bản ghi engagement.locked trong audit log. / The audit log has no engagement.locked entry.");
        }
        else if (ReadLockDetail(locked.PayloadJson) is not { } detail)
        {
            problems.Add("Bản ghi khoá không đọc được. / The lock entry cannot be read.");
        }
        else
        {
            if (!string.Equals(detail.Digest, lockDigest, StringComparison.Ordinal))
            {
                problems.Add($"Dữ liệu khác với lúc khoá (digest lúc khoá {detail.Digest}, hiện tại {lockDigest}). / The data differs from what was locked.");
            }

            if (detail.Papers != papers.Count)
            {
                problems.Add($"Lúc khoá có {detail.Papers} giấy làm việc, hiện có {papers.Count}. / The paper count differs from the lock.");
            }
        }

        foreach (var paper in papers)
        {
            try
            {
                if (!string.Equals(FieldDocument.FromJson(paper.State).ComputeContentHash(), paper.ContentHash, StringComparison.Ordinal))
                {
                    problems.Add($"{paper.TemplateCode} ({paper.DocumentId:D}): nội dung không khớp mã băm đã lưu. / content does not match its stored hash.");
                }
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
            {
                problems.Add($"{paper.TemplateCode} ({paper.DocumentId:D}): không đọc được trạng thái ({e.Message}). / state cannot be read.");
            }
        }

        foreach (var row in audit)
        {
            var entry = new AuditEntry(row.Sequence, row.OccurredAt, row.ActorUserId, row.Action, row.EntityType, row.EntityId, row.EngagementId,
                row.PayloadJson, row.PreviousHash, row.Hash);
            if (!string.Equals(HashChain.ComputeHash(entry), row.Hash, StringComparison.Ordinal))
            {
                problems.Add($"Audit log #{row.Sequence} ({row.Action}): nội dung không khớp mã băm. / entry does not match its hash.");
            }
        }

        foreach (var hash in hashes)
        {
            if (!objects.TryGetValue(hash, out _))
            {
                problems.Add($"Bằng chứng {hash[..Math.Min(16, hash.Length)]}…: có biên nhận nhưng không có bản ghi đối tượng. / receipt without an object record.");
            }
            else if (!await store.ExistsAsync(hash, cancellationToken))
            {
                problems.Add($"Bằng chứng {hash[..Math.Min(16, hash.Length)]}…: thiếu trên kho. Khôi phục từ R2 (RESTORE.md) rồi chạy lại. / missing from the store.");
            }
        }

        return problems;
    }

    private static (string? Digest, int Papers)? ReadLockDetail(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var detail = doc.RootElement.GetProperty("detail");
            return (detail.GetProperty("digest").GetString(), detail.GetProperty("papers").GetInt32());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static ArchiveEntry Json(string path, object value) => ArchiveEntry.FromBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Pretty));

    private static byte[] Lines<T>(IEnumerable<T> rows) =>
        Encoding.UTF8.GetBytes(string.Concat(rows.Select(r => JsonSerializer.Serialize(r, Compact) + "\n")));

    private static object Manifest(
        EngagementRow e, DateTimeOffset generatedAt, string lockDigest, long head, long auditHeadSequence, string auditHeadHash,
        int papers, int changes, int auditEntries, int evidenceFiles, long evidenceBytes) => new
        {
            formatVersion = FormatVersion,
            generatedAt,
            generatedBy = "audit-master " + (typeof(ArchivePackager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev"),
            engagement = new { id = e.Id, code = e.Code, clientName = e.ClientName, contractReference = e.ContractReference, periodStart = e.PeriodStart, periodEnd = e.PeriodEnd },
            lockedAt = e.LockedAt,
            lockedBy = e.LockedBy,
            lockDigest,
            changeLogHead = head,
            counts = new { papers, changes, auditEntries, evidenceFiles, evidenceBytes },
            auditChainAtPackaging = new { headSequence = auditHeadSequence, headHash = auditHeadHash },
            checksums = "SHA256SUMS lists every other file; its SHA-256 is the package digest.",
        };

    private static object EngagementDocument(EngagementRow e, Dictionary<Guid, UserRow> users) => new
    {
        id = e.Id, code = e.Code, clientName = e.ClientName, contractReference = e.ContractReference,
        periodStart = e.PeriodStart, periodEnd = e.PeriodEnd, status = e.Status.ToString(), lockedAt = e.LockedAt,
        lockedBy = e.LockedBy is { } by ? new { id = by, name = users.GetValueOrDefault(by)?.DisplayName, email = users.GetValueOrDefault(by)?.Email } : null,
        members = e.Members.OrderBy(m => m.UserId).ThenBy(m => m.Role).Select(m => new
        {
            userId = m.UserId, name = users.GetValueOrDefault(m.UserId)?.DisplayName, email = users.GetValueOrDefault(m.UserId)?.Email, role = m.Role.ToString(),
        }),
    };

    private static string Readme(EngagementRow e) => $"""
        Hồ sơ kiểm toán lưu trữ / Archived audit file
        Engagement {e.Code} — {e.ClientName} ({e.ContractReference}), {e.PeriodStart:yyyy-MM-dd} .. {e.PeriodEnd:yyyy-MM-dd}

        Gói này là bản đầy đủ của engagement đã khoá, không cần phần mềm của chúng tôi để đọc hay kiểm tra.
        This package is the complete record of a locked engagement. You need none of our software to read or check it.

        Kiểm tra / Verify:   sha256sum -c engagement-*/SHA256SUMS      (chạy ở thư mục chứa thư mục engagement-*, run from the folder that holds engagement-*)

        manifest.json        Tóm tắt, digest lúc khoá, số lượng. / Summary, lock digest, counts.
        engagement.json      Thông tin engagement và thành viên. / The engagement and its members.
        papers/*.json        Mỗi giấy làm việc: trạng thái, mã băm nội dung, toàn bộ dữ liệu (ô, ghi chú, xung đột, phê duyệt). / Each working paper in full.
        changes.jsonl        Nhật ký thay đổi theo thứ tự nhận (payload là base64 của JSON). / The change log in order of receipt (payload is base64 of JSON).
        audit-log.jsonl      Các dòng audit log về engagement này, kèm mã băm chuỗi. / Audit-log entries about this engagement, with their hash chain.
        evidence/index.json  Biên nhận từng file bằng chứng. / Receipt of every evidence file.
        evidence/files/<blake3>   File bằng chứng; tên file là BLAKE3-256 của nội dung (địa chỉ dùng trong mọi bản ghi). / Evidence files, named by the BLAKE3-256 of their content (the address every record uses).
        SHA256SUMS           Mã băm của mọi file khác trong gói. / The SHA-256 of every other file.
        """;

    private async Task WriteAuditAsync(Actor actor, string action, EngagementRow engagement, object detail, CancellationToken cancellationToken)
    {
        await auditLog.AppendAsync(new AuditEvent(clock.GetUtcNow(), actor.UserId, action, "engagement", engagement.Id.ToString("D"), engagement.Id,
            JsonSerializer.Serialize(new { via = actor.Via, address = actor.Address, code = engagement.Code, detail }, JsonSerializerOptions.Web)), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
