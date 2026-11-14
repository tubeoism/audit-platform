using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Evidence;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Evidence;

/// <summary>Opens a file with the program Windows associates with it.</summary>
public interface IFileLauncher
{
    void Open(string path);
}

/// <summary>
/// Evidence on this laptop (ADR-0004, "Evidence"). Attaching a file needs no connection: the bytes go into the
/// content-addressed store beside the database and the link goes to the outbox. The file is sent to the master
/// before the link is, so the shared record never points at a file the master cannot produce. Files attached by
/// others are fetched when someone opens them, and checked against their hash before they are shown.
/// </summary>
public sealed class LocalEvidence(ClientOptions options, IEvidenceTransport transport, IFileLauncher? launcher = null)
{
    private FileSystemEvidenceStore Store => field ??= new FileSystemEvidenceStore(options.EvidenceCachePath);

    /// <summary>True when this laptop holds the bytes of the file.</summary>
    public bool IsHere(string hash) => EvidenceHash.IsValid(hash) && File.Exists(Path.Combine(options.EvidenceCachePath, hash[..2], hash[2..4], hash));

    /// <summary>
    /// Takes a copy of a file into the local store and queues its link to the working paper. The original is not
    /// touched and may be moved or deleted afterwards. Returns the content hash.
    /// </summary>
    public async Task<string> AttachAsync(LocalUnit unit, Guid engagementId, Guid userId, Guid documentId, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"Không tìm thấy file “{path}”.");
        }

        if (file.Length == 0)
        {
            throw new InvalidOperationException($"File “{file.Name}” rỗng nên không được nhận làm bằng chứng.");
        }

        if (file.Length > options.MaxEvidenceBytes)
        {
            throw new InvalidOperationException(
                $"File “{file.Name}” nặng {Size(file.Length)}, vượt giới hạn {Size(options.MaxEvidenceBytes)} cho một file. Hãy tách hoặc nén file.");
        }

        var workspace = unit.Workspace(engagementId, userId);
        EvidenceRef stored;
        try
        {
            // The auditor may still have the file open in Excel or a PDF reader.
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            stored = await Store.PutAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Không đọc được file “{file.Name}”: {ex.Message}", ex);
        }

        if ((await workspace.ViewPaperAsync(documentId, cancellationToken)).Evidence.Contains(stored.Hash))
        {
            throw new InvalidOperationException($"File “{file.Name}” (đúng nội dung này) đã được đính kèm vào giấy làm việc.");
        }

        var row = await unit.Db.Evidence.FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.Hash == stored.Hash, cancellationToken);
        if (row is null)
        {
            unit.Db.Evidence.Add(new LocalEvidenceRow
            {
                EngagementId = engagementId, Hash = stored.Hash, FileName = file.Name, SizeBytes = stored.SizeBytes, AddedBy = userId,
                AddedAt = DateTimeOffset.UtcNow, Upload = EvidenceUpload.Pending,
            });
        }
        else if (row.Upload == EvidenceUpload.Refused)
        {
            // A second try by the user (after a role change, for example) is a new request.
            row.Upload = EvidenceUpload.Pending;
            row.LastError = null;
        }

        // The link is saved with the row: EnqueueAsync saves the context.
        await workspace.LinkEvidenceAsync(documentId, stored.Hash, cancellationToken);
        return stored.Hash;
    }

    /// <summary>
    /// Sends the files of one engagement that the master does not hold yet. Stops at the first one that does not get
    /// through; a file the master refuses is marked and its links are returned to their author with the reason.
    /// </summary>
    public async Task<(int Sent, string? Error)> UploadPendingAsync(LocalDbContext db, Guid engagementId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sent = 0;
        var rows = await db.Evidence.Where(e => e.EngagementId == engagementId && e.Upload == EvidenceUpload.Pending).ToListAsync(cancellationToken);
        foreach (var row in rows.OrderBy(r => r.AddedAt))
        {
            row.Attempts++;
            EvidenceSendResult result;
            try
            {
                await using var content = await Store.OpenVerifiedAsync(row.Hash, cancellationToken);
                result = await transport.SendAsync(engagementId, row.Hash, row.FileName, content, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or EvidenceIntegrityException)
            {
                result = new EvidenceSendResult(EvidenceSend.Refused, "Bản sao trên máy này đã mất hoặc bị thay đổi; hãy đính kèm lại file gốc.");
            }

            switch (result.Outcome)
            {
                case EvidenceSend.Stored:
                    row.Upload = EvidenceUpload.AtMaster;
                    row.LastError = null;
                    sent++;
                    break;
                case EvidenceSend.Refused:
                    row.Upload = EvidenceUpload.Refused;
                    row.LastError = result.Reason;
                    foreach (var change in await ChangesAboutAsync(db, engagementId, h => h == row.Hash, cancellationToken))
                    {
                        change.State = OutboxState.Rejected;
                        change.LastError = $"Máy chủ không nhận file “{row.FileName}”: {result.Reason}";
                    }

                    break;
                default:
                    row.LastError = result.Reason;
                    await db.SaveChangesAsync(cancellationToken);
                    return (sent, $"Chưa tải được file “{row.FileName}” lên máy chủ; sẽ thử lại.");
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return (sent, null);
    }

    /// <summary>
    /// Unsent links and removals that must wait: their file is not at the master yet. Everything else in the outbox
    /// goes ahead, so a large file on a slow line does not hold back the text of the working papers.
    /// </summary>
    public static async Task<List<long>> HeldChangesAsync(LocalDbContext db, Guid engagementId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var waiting = (await db.Evidence.AsNoTracking()
                .Where(e => e.EngagementId == engagementId && e.Upload != EvidenceUpload.AtMaster)
                .Select(e => e.Hash)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        return waiting.Count == 0 ? [] : [.. (await ChangesAboutAsync(db, engagementId, waiting.Contains, cancellationToken)).Select(c => c.LocalSequence)];
    }

    /// <summary>
    /// Learns the names of files that working papers link to and this laptop has not heard of (attached by a
    /// colleague). Asks the master only when there is such a file. Returns true when something was learnt.
    /// </summary>
    public async Task<bool> RefreshAsync(LocalDbContext db, Guid engagementId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Evidence.Where(e => e.EngagementId == engagementId).ToDictionaryAsync(e => e.Hash, cancellationToken);
        var linked = (await db.Documents.AsNoTracking().Where(d => d.EngagementId == engagementId).Select(d => d.State).ToListAsync(cancellationToken))
            .SelectMany(state => FieldDocument.FromJson(state).Evidence)
            .ToHashSet(StringComparer.Ordinal);
        if (linked.All(h => rows.TryGetValue(h, out var known) && known.Upload == EvidenceUpload.AtMaster)
            || await transport.ListAsync(engagementId, cancellationToken) is not { } listed)
        {
            return false;
        }

        var changed = false;
        foreach (var info in listed)
        {
            if (!rows.TryGetValue(info.Hash, out var row))
            {
                db.Evidence.Add(new LocalEvidenceRow
                {
                    EngagementId = engagementId, Hash = info.Hash, FileName = info.FileName, SizeBytes = info.SizeBytes, AddedBy = info.ReceivedBy,
                    AddedAt = info.ReceivedAt, Upload = EvidenceUpload.AtMaster,
                });
                changed = true;
            }
            else if (row.Upload != EvidenceUpload.AtMaster)
            {
                // A colleague sent the same content first.
                row.Upload = EvidenceUpload.AtMaster;
                row.LastError = null;
                changed = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Makes a file readable under its own name and returns the path of that copy: from the local store if the bytes
    /// are here, otherwise fetched from the master first. The bytes are checked against the hash either way.
    /// </summary>
    public async Task<string> MaterialiseAsync(LocalDbContext db, Guid engagementId, string hash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.Evidence.AsNoTracking().FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.Hash == hash, cancellationToken);
        if (!IsHere(hash))
        {
            var temp = Path.Combine(options.EvidenceCachePath, ".staging", "fetch-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            try
            {
                await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
                if (!await transport.FetchAsync(engagementId, hash, file, cancellationToken))
                {
                    throw new InvalidOperationException("File này chưa có trên máy bạn và chưa tải được từ máy chủ. Hãy thử lại khi có kết nối.");
                }

                file.Position = 0;
                await Store.PutAsync(file, hash, cancellationToken);
            }
            catch (EvidenceIntegrityException)
            {
                throw new InvalidOperationException("Nội dung tải về không khớp mã băm của bằng chứng nên đã bị loại bỏ. Hãy báo cho Trưởng nhóm.");
            }
            finally
            {
                File.Delete(temp);
            }
        }

        var name = SafeName(row?.FileName ?? hash[..16]);
        var folder = Path.Combine(options.EvidenceViewPath, hash[..16]);
        var target = Path.Combine(folder, name);
        try
        {
            await using var verified = await Store.OpenVerifiedAsync(hash, cancellationToken);
            if (!File.Exists(target) || new FileInfo(target).Length != verified.Length)
            {
                System.IO.Directory.CreateDirectory(folder);
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                }

                await using (var copy = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await verified.CopyToAsync(copy, cancellationToken);
                }

                MarkAsFromOutside(target);

                // A reading copy: edits made in it would not be evidence, so the program opening it is told not to offer any.
                File.SetAttributes(target, FileAttributes.ReadOnly);
            }
        }
        catch (EvidenceIntegrityException)
        {
            throw new InvalidOperationException("Bản sao bằng chứng trên máy này đã bị thay đổi so với mã băm. Hãy báo cho Trưởng nhóm.");
        }

        return target;
    }

    /// <summary>Materialises a file and opens it with its Windows program.</summary>
    public async Task OpenAsync(LocalDbContext db, Guid engagementId, string hash, CancellationToken cancellationToken)
    {
        var path = await MaterialiseAsync(db, engagementId, hash, cancellationToken);
        launcher?.Open(path);
    }

    /// <summary>Removes the reading copies made by <see cref="MaterialiseAsync"/>. The store itself is never emptied here.</summary>
    public void ClearReadingCopies()
    {
        if (!System.IO.Directory.Exists(options.EvidenceViewPath))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(options.EvidenceViewPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in a viewer; it goes next time.
            }
        }
    }

    /// <summary>
    /// False for file types Windows would run instead of display. Evidence comes from the auditee and, in an
    /// investigation, from the person investigated: such a file is shown in its folder, never started.
    /// </summary>
    public static bool OpensAsDocument(string path)
    {
        string[] runs =
        [
            ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp",
            ".msc", ".cpl", ".dll", ".jar", ".lnk", ".url", ".reg", ".inf", ".appx", ".msix", ".application", ".appref-ms", ".chm", ".iso", ".img",
            ".vhd", ".vhdx", ".py", ".sh",
        ];
        return !runs.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Marks the reading copy as coming from the internet zone (Windows "mark of the web"), so Office opens it in
    /// Protected View with macros off and Windows warns before running anything.
    /// </summary>
    private static void MarkAsFromOutside(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not an NTFS volume: the file is still only a reading copy.
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string([.. name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        return clean.Length == 0 ? "evidence" : clean.Length > 150 ? clean[..100] + Path.GetExtension(clean) : clean;
    }

    private static async Task<List<OutboxChangeRow>> ChangesAboutAsync(LocalDbContext db, Guid engagementId, Func<string, bool> hash, CancellationToken cancellationToken)
    {
        var pending = await db.Outbox
            .Where(o => o.EngagementId == engagementId && o.State == OutboxState.Pending && o.PayloadKind == FieldPayload.Kind)
            .ToListAsync(cancellationToken);
        return [.. pending.Where(o =>
        {
            try
            {
                return FieldPayload.Decode(o.Payload) switch
                {
                    LinkEvidence link => hash(link.ContentHash),
                    UnlinkEvidence unlink => hash(unlink.ContentHash),
                    _ => false,
                };
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        })];
    }
}
