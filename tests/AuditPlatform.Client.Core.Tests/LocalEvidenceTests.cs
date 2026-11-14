using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.Evidence;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Contracts.Evidence;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Evidence;
using AuditPlatform.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Tests;

/// <summary>Evidence on one laptop against a scripted master: what happens to files and their links on a bad line.</summary>
public sealed class LocalEvidenceTests : IAsyncDisposable
{
    private static readonly Guid Eng = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Paper = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "audit-evidence-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _wire = [];
    private readonly Factory _contexts;
    private readonly Line _line;
    private readonly Files _files;
    private readonly Launcher _launcher = new();
    private readonly LocalUnitFactory _units;
    private readonly LocalEvidence _evidence;
    private readonly SyncLoop _loop;

    public LocalEvidenceTests()
    {
        System.IO.Directory.CreateDirectory(_directory);
        var options = new ClientOptions { ClientId = Guid.NewGuid(), DataDirectory = _directory, MaxEvidenceBytes = 4096 };
        _contexts = new Factory(options.DatabasePath);
        using (var db = _contexts.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _line = new Line(_wire);
        _files = new Files(_wire);
        _units = new LocalUnitFactory(_contexts, _line, options, TimeProvider.System);
        _evidence = new LocalEvidence(options, _files, _launcher);
        _loop = new SyncLoop(_units, _line, new Source(), options, _evidence);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        System.IO.Directory.Delete(_directory, recursive: true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<string> AttachAsync(string name, string content)
    {
        await using var unit = await _units.OpenAsync(Ct);
        return await _evidence.AttachAsync(unit, Eng, Me, Paper, Write(name, content), Ct);
    }

    /// <summary>The laptop knows the engagement and has a paper that is not sent yet.</summary>
    private async Task StartAsync()
    {
        await _loop.RunOnceAsync(Ct);
        await using var unit = await _units.OpenAsync(Ct);
        await unit.Workspace(Eng, Me).CreatePaperAsync(Paper, "AUP-CSH-03", 1, Ct);
    }

    [Fact]
    public async Task A_file_that_does_not_get_through_holds_back_only_its_own_link()
    {
        await StartAsync();
        _files.Outcome = new EvidenceSendResult(EvidenceSend.Unreachable);
        var hash = await AttachAsync("count.pdf", "cash count");
        await using (var unit = await _units.OpenAsync(Ct))
        {
            await unit.Workspace(Eng, Me).SetFieldAsync(Paper, "result.exceptions", "0", Ct);
        }

        await _loop.RunOnceAsync(Ct);

        // The paper and its text went; the link waits for its file, and the status says which file.
        Assert.Equal(["create", "set"], _line.PushedOps);
        Assert.Equal((1, 1), (_loop.Status.Pending, _loop.Status.PendingFiles));
        Assert.Contains("count.pdf", _loop.Status.Error, StringComparison.Ordinal);

        _files.Outcome = new EvidenceSendResult(EvidenceSend.Stored);
        await _loop.RunOnceAsync(Ct);

        // On the wire the file comes before the link, never after.
        Assert.Equal(["create", "set", "link"], _line.PushedOps);
        Assert.True(_wire.IndexOf("file " + hash) < _wire.IndexOf("push link"));
        Assert.Equal((0, 0, null), (_loop.Status.Pending, _loop.Status.PendingFiles, _loop.Status.Error));

        // Once the master has the file it is not sent again: the failed attempt and the successful one, no third.
        await _loop.RunOnceAsync(Ct);
        Assert.Equal(2, _wire.Count(w => w == "file " + hash));
    }

    [Fact]
    public async Task A_file_the_master_refuses_returns_its_link_to_the_author_and_is_not_sent_again()
    {
        await StartAsync();
        _files.Outcome = new EvidenceSendResult(EvidenceSend.Refused, "A file may be at most 95 MB.");
        var hash = await AttachAsync("scan.pdf", "a large scan");

        await _loop.RunOnceAsync(Ct);
        await _loop.RunOnceAsync(Ct);

        Assert.Equal(["create"], _line.PushedOps);
        Assert.Single(_wire, w => w == "file " + hash);
        Assert.Equal((0, 0, 1), (_loop.Status.Pending, _loop.Status.PendingFiles, _loop.Status.NeedsAttention));
        await using var unit = await _units.OpenAsync(Ct);
        var view = await unit.Workspace(Eng, Me).ViewPaperAsync(Paper, Ct);
        Assert.Empty(view.Evidence);
        var refused = Assert.Single(view.Attention);
        Assert.IsType<LinkEvidence>(refused.Op);
        Assert.Contains("scan.pdf", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("at most 95 MB", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_and_its_removal_made_offline_wait_together_and_leave_nothing_on_the_paper()
    {
        await StartAsync();
        _files.Outcome = new EvidenceSendResult(EvidenceSend.Unreachable);
        var hash = await AttachAsync("draft.xlsx", "wrong file");
        await using (var unit = await _units.OpenAsync(Ct))
        {
            await unit.Workspace(Eng, Me).UnlinkEvidenceAsync(Paper, hash, Ct);
            Assert.Empty((await unit.Workspace(Eng, Me).ViewPaperAsync(Paper, Ct)).Evidence);
        }

        await _loop.RunOnceAsync(Ct);
        Assert.Equal(["create"], _line.PushedOps);

        _files.Outcome = new EvidenceSendResult(EvidenceSend.Stored);
        await _loop.RunOnceAsync(Ct);
        Assert.Equal(["create", "link", "unlink"], _line.PushedOps);
    }

    [Fact]
    public async Task What_cannot_be_evidence_is_refused_when_it_is_attached()
    {
        await StartAsync();
        var hash = await AttachAsync("count.pdf", "cash count");

        Assert.Contains("rỗng", (await Assert.ThrowsAsync<InvalidOperationException>(() => AttachAsync("empty.txt", ""))).Message, StringComparison.Ordinal);
        Assert.Contains("vượt giới hạn", (await Assert.ThrowsAsync<InvalidOperationException>(() => AttachAsync("big.bin", new string('x', 5000)))).Message, StringComparison.Ordinal);
        // Same content under another name is the same evidence.
        Assert.Contains("đã được đính kèm", (await Assert.ThrowsAsync<InvalidOperationException>(() => AttachAsync("copy of count.pdf", "cash count"))).Message, StringComparison.Ordinal);
        await using var unit = await _units.OpenAsync(Ct);
        Assert.Contains("Không tìm thấy", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => _evidence.AttachAsync(unit, Eng, Me, Paper, Path.Combine(_directory, "missing.pdf"), Ct))).Message, StringComparison.Ordinal);
        Assert.Equal([hash], (await unit.Workspace(Eng, Me).ViewPaperAsync(Paper, Ct)).Evidence);
        Assert.Equal(1, await unit.Db.Evidence.CountAsync(Ct));
    }

    [Fact]
    public async Task A_file_from_the_master_is_checked_against_its_hash_before_anyone_sees_it()
    {
        await StartAsync();
        var genuine = "bank confirmation"u8.ToArray();
        var hash = (await EvidenceHash.ComputeAsync(new MemoryStream(genuine), null, Ct)).Hash;
        await using var unit = await _units.OpenAsync(Ct);
        unit.Db.Evidence.Add(new LocalEvidenceRow
        {
            EngagementId = Eng, Hash = hash, FileName = "Xác nhận ngân hàng?.pdf", SizeBytes = genuine.Length, AddedBy = Guid.NewGuid(),
            AddedAt = DateTimeOffset.UtcNow, Upload = EvidenceUpload.AtMaster,
        });
        await unit.Db.SaveChangesAsync(Ct);

        // Offline: said plainly, nothing opened.
        Assert.Contains("chưa tải được", (await Assert.ThrowsAsync<InvalidOperationException>(() => _evidence.OpenAsync(unit.Db, Eng, hash, Ct))).Message, StringComparison.Ordinal);

        // Something else arrives under that address: thrown away, nothing opened, nothing kept.
        _files.Stored[hash] = "bank confirmation (edited)"u8.ToArray();
        Assert.Contains("không khớp mã băm", (await Assert.ThrowsAsync<InvalidOperationException>(() => _evidence.OpenAsync(unit.Db, Eng, hash, Ct))).Message, StringComparison.Ordinal);
        Assert.False(_evidence.IsHere(hash));
        Assert.Empty(_launcher.Paths);

        _files.Stored[hash] = genuine;
        await _evidence.OpenAsync(unit.Db, Eng, hash, Ct);
        var opened = Assert.Single(_launcher.Paths);
        Assert.Equal("Xác nhận ngân hàng_.pdf", Path.GetFileName(opened));
        Assert.Equal(genuine, await File.ReadAllBytesAsync(opened, Ct));

        // The copy in the local store is changed behind the application's back: it is not shown again.
        var stored = Path.Combine(_directory, "evidence", hash[..2], hash[2..4], hash);
        File.SetAttributes(stored, FileAttributes.Normal);
        await File.WriteAllTextAsync(stored, "tampered", Ct);
        Assert.Contains("đã bị thay đổi", (await Assert.ThrowsAsync<InvalidOperationException>(() => _evidence.OpenAsync(unit.Db, Eng, hash, Ct))).Message, StringComparison.Ordinal);
        Assert.Single(_launcher.Paths);

        // Reading copies are swept at start-up; the store is not.
        _evidence.ClearReadingCopies();
        Assert.False(File.Exists(opened));
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public async Task Names_of_files_attached_by_others_are_asked_for_only_when_a_paper_links_to_an_unknown_file()
    {
        await StartAsync();
        await _loop.RunOnceAsync(Ct);
        Assert.Equal(0, _files.Lists);

        var other = Guid.NewGuid();
        var hash = new string('e', 64);
        _files.Listed.Add(new EvidenceInfo(hash, 12, "Sổ quỹ.xlsx", null, other, DateTimeOffset.UtcNow));
        _line.Pages.Enqueue(new PullResult(
        [
            new ChangeEnvelope(Paper, Eng, Paper, Me, Guid.NewGuid(), DateTimeOffset.UtcNow, FieldPayload.Kind,
                FieldPayload.Encode(new CreatePaper("AUP-CSH-03", 1, Me)), 1, DateTimeOffset.UtcNow),
            new ChangeEnvelope(Guid.NewGuid(), Eng, Paper, other, Guid.NewGuid(), DateTimeOffset.UtcNow, FieldPayload.Kind,
                FieldPayload.Encode(new LinkEvidence(hash)), 2, DateTimeOffset.UtcNow),
        ], new SyncCursor(2), false));

        await _loop.RunOnceAsync(Ct);
        await _loop.RunOnceAsync(Ct);

        Assert.Equal(1, _files.Lists);
        await using var unit = await _units.OpenAsync(Ct);
        var row = await unit.Db.Evidence.SingleAsync(Ct);
        Assert.Equal(("Sổ quỹ.xlsx", EvidenceUpload.AtMaster, other), (row.FileName, row.Upload, row.AddedBy));
    }

    [Theory]
    [InlineData("report.pdf", true), InlineData("ledger.XLSX", true), InlineData("photo.jpg", true), InlineData("noextension", true)]
    [InlineData("invoice.pdf.exe", false), InlineData("run.BAT", false), InlineData("shortcut.lnk", false), InlineData("macro.js", false)]
    public void A_file_windows_would_run_is_never_started(string name, bool opens) =>
        Assert.Equal(opens, LocalEvidence.OpensAsDocument(Path.Combine("C:", "evidence", name)));

    private sealed class Factory(string path) : IDbContextFactory<LocalDbContext>
    {
        public LocalDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    private sealed class Launcher : IFileLauncher
    {
        public List<string> Paths { get; } = [];

        public void Open(string path) => Paths.Add(path);
    }

    private sealed class Source : IDirectorySource
    {
        public Task<DirectorySnapshot> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(new DirectorySnapshot(
            [new EngagementInfo(Eng, "ENG-1", "Client A", "AUP-1", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "Fieldwork", false,
                [new EngagementMemberInfo(Me, "An", ["Preparer"])])],
            []));
    }

    /// <summary>The master's file API, scripted.</summary>
    private sealed class Files(List<string> wire) : IEvidenceTransport
    {
        public EvidenceSendResult Outcome { get; set; } = new(EvidenceSend.Stored);
        public Dictionary<string, byte[]> Stored { get; } = [];
        public List<EvidenceInfo> Listed { get; } = [];
        public int Lists { get; private set; }

        public Task<EvidenceSendResult> SendAsync(Guid engagementId, string hash, string fileName, Stream content, CancellationToken cancellationToken)
        {
            wire.Add("file " + hash);
            return Task.FromResult(Outcome);
        }

        public Task<IReadOnlyList<EvidenceInfo>?> ListAsync(Guid engagementId, CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult<IReadOnlyList<EvidenceInfo>?>(Listed);
        }

        public async Task<bool> FetchAsync(Guid engagementId, string hash, Stream destination, CancellationToken cancellationToken)
        {
            if (!Stored.TryGetValue(hash, out var bytes))
            {
                return false;
            }

            await destination.WriteAsync(bytes, cancellationToken);
            return true;
        }
    }

    /// <summary>The sync connection: always online, takes every change into a log like the master's and hands it back on pull, or the pages a test queued.</summary>
    private sealed class Line(List<string> wire) : ISyncTransport
    {
        public ConnectionState State { get; private set; }
        public List<string> PushedOps { get; } = [];
        public Queue<PullResult> Pages { get; } = new();
        private readonly List<ChangeEnvelope> _log = [];

        public event EventHandler<ConnectionState>? StateChanged;
        public event EventHandler<Guid>? ChangesAvailable { add { } remove { } }
        public event EventHandler<Guid>? EngagementLocked { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            State = ConnectionState.Online;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(Guid engagementId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PushResult> PushAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
        {
            foreach (var change in changes)
            {
                var name = FieldPayload.Decode(change.Payload).Name;
                PushedOps.Add(name);
                wire.Add("push " + name);
                _log.Add(change with { Sequence = _log.Count + 1, ReceivedAt = DateTimeOffset.UnixEpoch });
            }

            return Task.FromResult(new PushResult([.. changes.Select(c => new ChangeResult(c.ChangeId, ChangeOutcome.Accepted))], SyncCursor.Start));
        }

        public Task<PullResult> PullAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
        {
            if (Pages.Count > 0)
            {
                return Task.FromResult(Pages.Dequeue());
            }

            var page = _log.Where(c => c.Sequence > since.Sequence).Take(maxChanges).ToList();
            return Task.FromResult(new PullResult(page, page.Count > 0 ? new SyncCursor(page[^1].Sequence) : since, false));
        }
    }
}
