using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuditPlatform.AuditLog;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Evidence;
using AuditPlatform.Master.Archive;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Engagements;
using AuditPlatform.Master.Tests.Auth;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditPlatform.Master.Tests.Archive;

/// <summary>
/// Packaging a locked engagement for the immutable archive and marking it archived, on the real master host and a real
/// PostgreSQL database. The engagement is seeded already locked, the way the lock leaves it: a paper whose state and
/// hash agree, a change, an evidence file in the real store, and the <c>engagement.locked</c> entry carrying the digest.
/// </summary>
public sealed class ArchiveFlowTests(AuthFactory factory) : IClassFixture<AuthFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Actor Console = Actor.Console("test");

    private sealed record Seed(Guid Id, string Code, Guid Manager, Guid Paper, string EvidenceHash, byte[] Evidence);

    private async Task<Seed> SeedAsync(EngagementStatus status = EngagementStatus.Locked, bool withLockEntry = true)
    {
        factory.RequireDatabase();
        var manager = await factory.EnrolAsync();
        var id = Guid.NewGuid();
        var code = "ARC-" + id.ToString("N")[..8];
        var paperId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("Bank confirmation, signed " + id);

        await factory.ScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MasterDbContext>();
            var now = factory.Clock.GetUtcNow();
            var stored = await sp.GetRequiredService<IEvidenceStore>().PutAsync(new MemoryStream(content), Ct);

            var doc = new FieldDocument(paperId, id);
            doc.Apply(Guid.NewGuid(), manager.Id, new CreatePaper("AUP-CSH-03", 1, manager.Id));
            doc.Apply(Guid.NewGuid(), manager.Id, new SetField("exceptions_noted", "0", null));
            doc.Apply(Guid.NewGuid(), manager.Id, new LinkEvidence(stored.Hash));
            var paper = new SyncDocumentRow
            {
                DocumentId = paperId, EngagementId = id, TemplateCode = "AUP-CSH-03", PreparerId = manager.Id, Status = "JapanDeskSignedOff",
                ContentHash = doc.ComputeContentHash(), OpenConflicts = 0, LastSequence = 1, UpdatedAt = now, State = doc.ToJson(),
            };

            db.Engagements.Add(new EngagementRow
            {
                Id = id, Code = code, ClientName = "Khách hàng Thử", ContractReference = "AUP-2026", PeriodStart = new DateOnly(2026, 1, 1),
                PeriodEnd = new DateOnly(2026, 12, 31), Status = status, LockedAt = status == EngagementStatus.Locked ? now : null,
                LockedBy = status == EngagementStatus.Locked ? manager.Id : null,
                Members = [new EngagementMemberRow { EngagementId = id, UserId = manager.Id, Role = EngagementRole.EngagementManager }],
            });
            db.SyncDocuments.Add(paper);
            db.Changes.Add(new ChangeRow
            {
                EngagementId = id, Sequence = 1, ChangeId = Guid.NewGuid(), DocumentId = paperId, AuthorUserId = manager.Id, ClientId = Guid.NewGuid(),
                ClientCreatedAt = now, ReceivedAt = now, PayloadKind = "set", Payload = Encoding.UTF8.GetBytes("{\"op\":\"set\"}"),
            });
            db.EvidenceObjects.Add(new EvidenceObjectRow { Hash = stored.Hash, SizeBytes = stored.SizeBytes, FirstReceivedAt = now });
            db.EvidenceIntake.Add(new EvidenceIntakeRow
            {
                Id = Guid.NewGuid(), Hash = stored.Hash, EngagementId = id, OriginalFileName = "xác nhận ngân hàng.pdf", MediaType = "application/pdf",
                ReceivedBy = manager.Id, ReceivedAt = now, Source = "client-upload",
            });
            await db.SaveChangesAsync(Ct);

            if (withLockEntry)
            {
                var digest = EngagementLifecycle.Digest(id, 1, [paper]);
                await sp.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(now, manager.Id, "engagement.locked", "engagement", id.ToString("D"), id,
                    JsonSerializer.Serialize(new { via = "api", address = (string?)null, code, detail = new { digest, head = 1, papers = 1 } }, JsonSerializerOptions.Web)), Ct);
                await db.SaveChangesAsync(Ct);
            }

            return 0;
        });

        // The store's address is BLAKE3-256, not SHA-256: ask the store.
        var hash = await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().EvidenceIntake.AsNoTracking()
            .Where(i => i.EngagementId == id).Select(i => i.Hash).SingleAsync(Ct));
        return new Seed(id, code, manager.Id, paperId, hash, content);
    }

    private static string NewPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "audit-archive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "package.tar");
    }

    private Task<Outcome<ArchivePackageInfo>> PackageAsync(string code, string path) =>
        factory.ScopeAsync(sp => sp.GetRequiredService<ArchivePackager>().BuildAsync(Console, code, path, Ct));

    private Task<List<AuditLogRow>> TrailAsync(Guid engagement) =>
        factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().AuditLog.AsNoTracking().Where(a => a.EngagementId == engagement).OrderBy(a => a.Sequence).ToListAsync(Ct));

    private static async Task<Dictionary<string, byte[]>> ReadTarAsync(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var stream = File.OpenRead(path);
        await using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync(cancellationToken: Ct) is { } entry)
        {
            using var copy = new MemoryStream();
            await entry.DataStream!.CopyToAsync(copy, Ct);
            files[entry.Name] = copy.ToArray();
        }

        return files;
    }

    [Fact]
    public async Task A_locked_engagement_is_packaged_with_everything_in_it_and_the_package_verifies()
    {
        var seed = await SeedAsync();
        var path = NewPath();

        var packaged = await PackageAsync(seed.Code, path);

        Assert.True(packaged.Succeeded, packaged.Error?.Message);
        var info = packaged.Value!;
        Assert.Equal((1, 1, 1, 1), (info.Papers, info.Changes, info.EvidenceFiles, info.AuditEntries));   // the audit entry is the lock
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))), info.TarSha256);

        var root = $"engagement-{seed.Id:D}";
        var files = await ReadTarAsync(path);
        Assert.Equal(
            new[] { "README.txt", "SHA256SUMS", "audit-log.jsonl", "changes.jsonl", "engagement.json", "evidence/files/" + seed.EvidenceHash, "evidence/index.json", "manifest.json", $"papers/{seed.Paper:D}.json" }
                .Select(n => $"{root}/{n}").Order(StringComparer.Ordinal),
            files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(seed.Evidence, files[$"{root}/evidence/files/{seed.EvidenceHash}"]);

        var sums = Encoding.UTF8.GetString(files[$"{root}/SHA256SUMS"]);
        Assert.Equal(info.PackageDigest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sums))));
        foreach (var line in sums.TrimEnd('\n').Split('\n'))
        {
            var parts = line.Split("  ", 2);
            Assert.Equal(parts[0], Convert.ToHexStringLower(SHA256.HashData(files[parts[1]])));
        }

        using var manifest = JsonDocument.Parse(files[$"{root}/manifest.json"]);
        Assert.Equal(info.LockDigest, manifest.RootElement.GetProperty("lockDigest").GetString());
        Assert.Equal(seed.Code, manifest.RootElement.GetProperty("engagement").GetProperty("code").GetString());
        using var members = JsonDocument.Parse(files[$"{root}/engagement.json"]);
        Assert.Equal("EngagementManager", members.RootElement.GetProperty("members")[0].GetProperty("role").GetString());
        Assert.Contains("\"engagement.locked\"", Encoding.UTF8.GetString(files[$"{root}/audit-log.jsonl"]), StringComparison.Ordinal);
        // The one instruction stored with the archive for years must be a command that works from the folder holding the package.
        Assert.Contains("sha256sum -c engagement-*/SHA256SUMS", Encoding.UTF8.GetString(files[$"{root}/README.txt"]), StringComparison.Ordinal);
        Assert.Contains("xác nhận ngân hàng.pdf", Encoding.UTF8.GetString(files[$"{root}/evidence/index.json"]), StringComparison.Ordinal);
        Assert.Contains(seed.EvidenceHash, Encoding.UTF8.GetString(files[$"{root}/evidence/index.json"]), StringComparison.Ordinal);
        // The checksum list holds the real SHA-256 of the evidence, which is not its (BLAKE3) name.
        Assert.Contains($"{Convert.ToHexStringLower(SHA256.HashData(seed.Evidence))}  {root}/evidence/files/{seed.EvidenceHash}", sums, StringComparison.Ordinal);

        // Packaging is read-only for the engagement, and it is on the audit log with the digests.
        var trail = await TrailAsync(seed.Id);
        var record = Assert.Single(trail, a => a.Action == "engagement.archive.packaged");
        Assert.Contains(info.PackageDigest, record.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(info.TarSha256, record.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(EngagementStatus.Locked, (await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().Engagements.AsNoTracking().SingleAsync(e => e.Id == seed.Id, Ct))).Status);
    }

    [Fact]
    public async Task Only_a_locked_engagement_with_a_free_output_path_is_packaged()
    {
        var open = await SeedAsync(EngagementStatus.Reporting, withLockEntry: false);
        Assert.Equal("not_locked", (await PackageAsync(open.Code, NewPath())).Error?.Code);
        Assert.Equal("engagement_not_found", (await PackageAsync("NO-SUCH-CODE", NewPath())).Error?.Code);

        var locked = await SeedAsync();
        var path = NewPath();
        Assert.True((await PackageAsync(locked.Code, path)).Succeeded);
        Assert.Equal("output_exists", (await PackageAsync(locked.Code, path)).Error?.Code);
        Assert.Equal("output_directory_missing", (await PackageAsync(locked.Code, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "x.tar"))).Error?.Code);
    }

    [Fact]
    public async Task Data_that_differs_from_what_was_locked_is_not_packaged()
    {
        var seed = await SeedAsync();
        await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().SyncDocuments.Where(d => d.DocumentId == seed.Paper)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ContentHash, new string('0', 64)), Ct));
        var path = NewPath();

        var refused = await PackageAsync(seed.Code, path);

        Assert.Equal("archive_blocked", refused.Error?.Code);
        Assert.Contains("khác với lúc khoá", refused.Error!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Single(await TrailAsync(seed.Id), a => a.Action == "engagement.archive.refused");
    }

    [Fact]
    public async Task An_engagement_without_a_lock_entry_in_the_audit_log_is_not_packaged()
    {
        var seed = await SeedAsync(withLockEntry: false);

        var refused = await PackageAsync(seed.Code, NewPath());

        Assert.Equal("archive_blocked", refused.Error?.Code);
        Assert.Contains("engagement.locked", refused.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_the_store_no_longer_holds_stops_the_package_before_anything_is_written()
    {
        var seed = await SeedAsync();
        var ghost = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("never stored " + seed.Id)));
        await factory.ScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MasterDbContext>();
            var now = factory.Clock.GetUtcNow();
            db.EvidenceObjects.Add(new EvidenceObjectRow { Hash = ghost, SizeBytes = 10, FirstReceivedAt = now });
            db.EvidenceIntake.Add(new EvidenceIntakeRow
            {
                Id = Guid.NewGuid(), Hash = ghost, EngagementId = seed.Id, OriginalFileName = "ghost.pdf", ReceivedBy = seed.Manager, ReceivedAt = now, Source = "client-upload",
            });
            return await db.SaveChangesAsync(Ct);
        });
        var path = NewPath();

        var refused = await PackageAsync(seed.Code, path);

        Assert.Equal("archive_blocked", refused.Error?.Code);
        Assert.Contains(ghost[..16], refused.Error!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Marking_archived_needs_a_package_the_master_built_and_cannot_be_repeated()
    {
        var seed = await SeedAsync();
        var path = NewPath();
        var info = (await PackageAsync(seed.Code, path)).Value!;
        Task<Outcome<ArchiveResult>> ArchiveAsync(string code, string package, string tar, string where) =>
            factory.ScopeAsync(sp => sp.GetRequiredService<EngagementLifecycle>().ArchiveAsync(Console, code, package, tar, where, Ct));
        const string Where = "r2:audit-archive/archive/engagements/x.tar";

        Assert.Equal("digest_invalid", (await ArchiveAsync(seed.Code, "abc", info.TarSha256, Where)).Error?.Code);
        Assert.Equal("location_invalid", (await ArchiveAsync(seed.Code, info.PackageDigest, info.TarSha256, "  ")).Error?.Code);
        Assert.Equal("engagement_not_found", (await ArchiveAsync("NO-SUCH", info.PackageDigest, info.TarSha256, Where)).Error?.Code);
        var invented = new string('b', 64);
        Assert.Equal("package_unknown", (await ArchiveAsync(seed.Code, invented, info.TarSha256, Where)).Error?.Code);
        Assert.Equal("package_unknown", (await ArchiveAsync(seed.Code, info.PackageDigest, invented, Where)).Error?.Code);
        Assert.Equal(EngagementStatus.Locked, (await EngagementAsync(seed.Id)).Status);

        var done = await ArchiveAsync(seed.Code, info.PackageDigest.ToUpperInvariant(), info.TarSha256, Where);
        Assert.True(done.Succeeded, done.Error?.Message);
        var row = await EngagementAsync(seed.Id);
        Assert.Equal(EngagementStatus.Archived, row.Status);
        Assert.Equal(seed.Manager, row.LockedBy);

        Assert.Equal("already_archived", (await ArchiveAsync(seed.Code, info.PackageDigest, info.TarSha256, Where)).Error?.Code);
        var trail = (await TrailAsync(seed.Id)).Select(a => a.Action).ToList();
        Assert.Equal(2, trail.Count(a => a == "engagement.archive.refused"));
        var archived = Assert.Single(await TrailAsync(seed.Id), a => a.Action == "engagement.archived");
        Assert.Contains(info.PackageDigest, archived.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(Where, archived.PayloadJson, StringComparison.Ordinal);

        // An archived engagement can still be packaged again (a second copy), and stays archived.
        Assert.True((await PackageAsync(seed.Code, NewPath())).Succeeded);
        Assert.Equal(EngagementStatus.Archived, (await EngagementAsync(seed.Id)).Status);
    }

    [Fact]
    public async Task An_engagement_that_is_not_locked_cannot_be_marked_archived()
    {
        var seed = await SeedAsync(EngagementStatus.Reporting, withLockEntry: false);

        var refused = await factory.ScopeAsync(sp => sp.GetRequiredService<EngagementLifecycle>()
            .ArchiveAsync(Console, seed.Code, new string('a', 64), new string('b', 64), "somewhere", Ct));

        Assert.Equal("not_locked", refused.Error?.Code);
        Assert.Equal(EngagementStatus.Reporting, (await EngagementAsync(seed.Id)).Status);
    }

    [Fact]
    public async Task The_console_commands_print_what_the_archive_script_reads_and_archive_only_a_known_package()
    {
        var seed = await SeedAsync();
        var path = NewPath();

        // `audit-engagement-archive` reads these two lines with awk ('$1 == "tar-sha256" { print $2 }'): keep their shape.
        var packaged = new StringWriter();
        Assert.Equal(0, await AdminCli.RunAsync(factory.Services, ["engagement", "package", seed.Code, path], packaged));
        var text = packaged.ToString();
        var tarSha = Regex.Match(text, "^tar-sha256 +([0-9a-f]{64})$", RegexOptions.Multiline).Groups[1].Value;
        var digest = Regex.Match(text, "^package-digest +([0-9a-f]{64})$", RegexOptions.Multiline).Groups[1].Value;
        Assert.NotEmpty(tarSha);
        Assert.NotEmpty(digest);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))), tarSha);

        var refused = new StringWriter();
        Assert.Equal(1, await AdminCli.RunAsync(factory.Services, ["engagement", "archived", seed.Code, digest, new string('b', 64), "somewhere"], refused));
        Assert.Contains("package_unknown", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal(EngagementStatus.Locked, (await EngagementAsync(seed.Id)).Status);

        var done = new StringWriter();
        Assert.Equal(0, await AdminCli.RunAsync(factory.Services, ["engagement", "archived", seed.Code, digest, tarSha, "r2:audit-archive/archive/engagements/x.tar.bin"], done));
        Assert.Contains("Archived", done.ToString(), StringComparison.Ordinal);
        Assert.Equal(EngagementStatus.Archived, (await EngagementAsync(seed.Id)).Status);

        var again = new StringWriter();
        Assert.Equal(1, await AdminCli.RunAsync(factory.Services, ["engagement", "archived", seed.Code, digest, tarSha, "x"], again));
        Assert.Contains("already_archived", again.ToString(), StringComparison.Ordinal);
    }

    private Task<EngagementRow> EngagementAsync(Guid id) =>
        factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().Engagements.AsNoTracking().SingleAsync(e => e.Id == id, Ct));
}
