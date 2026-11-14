using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Tests;

public sealed class SyncLoopTests : IAsyncDisposable
{
    private static readonly Guid Eng = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "audit-loop-" + Guid.NewGuid().ToString("N"));
    private readonly Factory _contexts;
    private readonly Transport _transport = new();
    private readonly Source _source = new();
    private readonly LocalUnitFactory _units;
    private readonly SyncLoop _loop;

    public SyncLoopTests()
    {
        System.IO.Directory.CreateDirectory(_directory);
        var options = new ClientOptions { ClientId = Guid.NewGuid(), DataDirectory = _directory };
        _contexts = new Factory(options.DatabasePath);
        using (var db = _contexts.CreateDbContext())
        {
            // The same path a real laptop takes: migrations, not EnsureCreated.
            db.Database.Migrate();
        }

        _units = new LocalUnitFactory(_contexts, _transport, options, TimeProvider.System);
        _loop = new SyncLoop(_units, _transport, _source, options);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync();
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(_directory, recursive: true);
    }

    private static EngagementInfo Info(bool readOnly = false) =>
        new(Eng, "ENG-1", "Client A", "AUP-1", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), readOnly ? "Locked" : "Fieldwork", readOnly,
            [new EngagementMemberInfo(Me, "An", ["Preparer", "EngagementManager"])]);

    private static ProcedureTemplateInfo Template(int version, string language = "vi", string title = "Kiểm quỹ") =>
        new("AUP-CSH-03", version, language, "CSH", "Quản lý tiền", title, "Kiểm kê quỹ đột xuất.", ["Đếm tiền."], ["Biên bản kiểm quỹ."]);

    [Fact]
    public async Task A_round_without_a_connection_reports_offline_and_keeps_the_work()
    {
        _transport.FailConnect = true;
        await using (var unit = await _units.OpenAsync(Ct))
        {
            await unit.Workspace(Eng, Me).CreatePaperAsync(Guid.NewGuid(), "AUP-CSH-03", 1, Ct);
        }

        await _loop.RunOnceAsync(Ct);

        Assert.Equal((ConnectionState.Offline, 1), (_loop.Status.Connection, _loop.Status.Pending));
        Assert.NotNull(_loop.Status.Error);
        Assert.Empty(_transport.Pushed);
    }

    [Fact]
    public async Task First_round_stores_the_directory_subscribes_and_pushes_and_later_rounds_do_not_repeat_it()
    {
        _source.Next = new DirectorySnapshot([Info()], [Template(1), Template(1, "en", "Cash Count"), Template(2)]);
        var paper = Guid.NewGuid();
        await using (var unit = await _units.OpenAsync(Ct))
        {
            await unit.Workspace(Eng, Me).CreatePaperAsync(paper, "AUP-CSH-03", 2, Ct);
        }

        var changed = 0;
        _loop.DataChanged += (_, _) => changed++;
        await _loop.RunOnceAsync(Ct);
        await _loop.RunOnceAsync(Ct);

        Assert.Equal((ConnectionState.Online, 0, null), (_loop.Status.Connection, _loop.Status.Pending, _loop.Status.Error));
        Assert.Equal([Eng], _transport.Subscribed);
        Assert.Equal(1, _source.Fetches);
        Assert.Equal(1, changed);
        Assert.Equal(paper, Assert.Single(_transport.Pushed).DocumentId);

        await using var db = _contexts.CreateDbContext();
        var member = Assert.Single(await db.Members.ToListAsync(Ct));
        Assert.True(member.RoleSet().SetEquals([Domain.Engagements.EngagementRole.Preparer, Domain.Engagements.EngagementRole.EngagementManager]));
        // New papers start from the newest version; an existing paper finds the wording of the version it pinned,
        // in the other language when the preferred one does not have it.
        Assert.Equal(2, Assert.Single(await DirectoryStore.LatestTemplatesAsync(db, "vi", Ct)).Version);
        Assert.Equal("Cash Count", (await DirectoryStore.FindTemplateAsync(db, "AUP-CSH-03", 1, "en", Ct))!.Title);
        Assert.Equal("Kiểm quỹ", (await DirectoryStore.FindTemplateAsync(db, "AUP-CSH-03", 2, "en", Ct))!.Title);
        Assert.Null(await DirectoryStore.FindTemplateAsync(db, "AUP-CSH-03", 9, "vi", Ct));
    }

    [Fact]
    public async Task After_a_reconnect_the_loop_subscribes_again_and_reads_the_directory_again()
    {
        _source.Next = new DirectorySnapshot([Info()], []);
        await _loop.RunOnceAsync(Ct);

        _transport.Drop();
        await _loop.RunOnceAsync(Ct);
        Assert.Equal([Eng], _transport.Subscribed);
        Assert.Equal(2, _source.Fetches);

        // The user was taken off the engagement while away: it leaves the list.
        _transport.Drop();
        _source.Next = new DirectorySnapshot([], []);
        await _loop.RunOnceAsync(Ct);
        Assert.Empty(_transport.Subscribed);
        await using var db = _contexts.CreateDbContext();
        Assert.Empty(await db.Engagements.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_directory_the_master_did_not_answer_is_asked_for_again_and_wipes_nothing()
    {
        _source.Next = new DirectorySnapshot([Info()], [Template(1)]);
        await _loop.RunOnceAsync(Ct);
        _transport.Drop();
        _source.Next = new DirectorySnapshot(null, null);

        await _loop.RunOnceAsync(Ct);
        await _loop.RunOnceAsync(Ct);

        Assert.Equal(3, _source.Fetches);
        await using var db = _contexts.CreateDbContext();
        Assert.Single(await db.Engagements.ToListAsync(Ct));
        Assert.Single(await db.Templates.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_lock_signal_makes_the_engagement_read_only_on_this_laptop()
    {
        _source.Next = new DirectorySnapshot([Info()], []);
        await _loop.RunOnceAsync(Ct);

        _transport.RaiseLocked(Eng);
        await _loop.RunOnceAsync(Ct);

        await using var unit = await _units.OpenAsync(Ct);
        Assert.True((await unit.Db.Engagements.SingleAsync(Ct)).IsReadOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Workspace(Eng, Me).CreatePaperAsync(Guid.NewGuid(), "AUP-CSH-03", 1, Ct));
    }

    [Fact]
    public async Task A_replica_that_cannot_follow_the_master_stops_and_says_so_instead_of_retrying()
    {
        _source.Next = new DirectorySnapshot([Info()], []);
        // Sequence 5 where 1 is expected: a gap in the log.
        _transport.Pages.Enqueue(new PullResult(
            [new ChangeEnvelope(Guid.NewGuid(), Eng, Guid.NewGuid(), Me, Guid.NewGuid(), DateTimeOffset.UnixEpoch, FieldPayload.Kind,
                FieldPayload.Encode(new CreatePaper("AUP-CSH-03", 1, Me)), Sequence: 5)],
            new SyncCursor(5), false));

        await _loop.RunOnceAsync(Ct);
        var pulls = _transport.Pulls;
        await _loop.RunOnceAsync(Ct);

        Assert.Contains("does not follow", _loop.Status.Error, StringComparison.Ordinal);
        Assert.Contains("does not follow", _loop.Stopped[Eng], StringComparison.Ordinal);
        Assert.Equal(pulls, _transport.Pulls);
        await using var db = _contexts.CreateDbContext();
        Assert.Empty(await db.ReplicaLog.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_next_pull_carries_the_id_of_the_last_change_the_replica_holds()
    {
        _source.Next = new DirectorySnapshot([Info()], []);
        var paper = Guid.NewGuid();
        await using (var unit = await _units.OpenAsync(Ct))
        {
            var work = unit.Workspace(Eng, Me);
            await work.CreatePaperAsync(paper, "AUP-CSH-03", 1, Ct);
            await work.SetFieldAsync(paper, "exceptions_noted", "0", Ct);
        }

        await _loop.RunOnceAsync(Ct);
        await _loop.RunOnceAsync(Ct);

        await using var db = _contexts.CreateDbContext();
        var last = await db.ReplicaLog.OrderByDescending(r => r.Sequence).FirstAsync(Ct);
        Assert.Equal(new SyncCursor(0, null), _transport.Cursors[0]);
        Assert.Equal(new SyncCursor(2, last.ChangeId), _transport.Cursors[^1]);
    }

    [Fact]
    public async Task A_restored_master_stops_the_replica_and_a_rebuild_resubmits_the_work_it_lost()
    {
        _source.Next = new DirectorySnapshot([Info()], []);
        var paper = Guid.NewGuid();
        await using (var unit = await _units.OpenAsync(Ct))
        {
            var work = unit.Workspace(Eng, Me);
            await work.CreatePaperAsync(paper, "AUP-CSH-03", 1, Ct);
            await work.SetFieldAsync(paper, "exceptions_noted", "0", Ct);
        }

        await _loop.RunOnceAsync(Ct);
        await _loop.RunOnceAsync(Ct);
        await using (var db = _contexts.CreateDbContext())
        {
            Assert.Equal((2, 0), (await db.ReplicaLog.CountAsync(Ct), await db.Outbox.CountAsync(Ct)));
        }

        // The master is restored from a backup older than this laptop's work, and says the replica must be rebuilt.
        _transport.ForgetLog();
        _transport.PullError = $"Failed to invoke 'Pull' due to an error on the server. HubException: {SyncErrors.ReplicaMustRebuild}: log differs.";
        await _loop.RunOnceAsync(Ct);

        Assert.Contains("dựng lại", _loop.Stopped[Eng], StringComparison.Ordinal);
        Assert.Contains("dựng lại", _loop.Status.Error, StringComparison.Ordinal);
        var pulls = _transport.Pulls;
        await _loop.RunOnceAsync(Ct);
        Assert.Equal(pulls, _transport.Pulls);

        // Rebuilding keeps everything the author did, and works while the master still refuses.
        var pushed = _transport.Pushed.Count;
        var rebuilt = await _loop.RebuildAsync(Eng, Ct);
        Assert.Equal(new ReplicaRebuild(Resubmitted: 2, PendingKept: 0), rebuilt);
        Assert.Empty(_loop.Stopped);
        await using (var db = _contexts.CreateDbContext())
        {
            Assert.Empty(await db.ReplicaLog.ToListAsync(Ct));
            Assert.Empty(await db.Documents.ToListAsync(Ct));
            Assert.Equal(2, await db.Outbox.CountAsync(o => o.State == OutboxState.Pending, Ct));
            Assert.Equal(0, (await db.SyncState.SingleAsync(Ct)).LastPulledSequence);
        }

        // The master takes the work again and the replica follows it from the start.
        _transport.PullError = null;
        await _loop.RunOnceAsync(Ct);
        Assert.Equal(pushed + 2, _transport.Pushed.Count);
        await using (var db = _contexts.CreateDbContext())
        {
            Assert.Equal((2, 0), (await db.ReplicaLog.CountAsync(Ct), await db.Outbox.CountAsync(Ct)));
            var doc = await new LocalReplica(db).GetDocumentAsync(Eng, paper, Ct);
            Assert.Equal("0", doc.Value("exceptions_noted"));
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<LocalDbContext>
    {
        public LocalDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    private sealed class Source : IDirectorySource
    {
        public DirectorySnapshot Next { get; set; } = new([], []);
        public int Fetches { get; private set; }

        public Task<DirectorySnapshot> FetchAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(Next);
        }
    }

    private sealed class Transport : ISyncTransport
    {
        public ConnectionState State { get; private set; }
        public bool FailConnect { get; set; }
        public List<Guid> Subscribed { get; private set; } = [];
        public List<ChangeEnvelope> Pushed { get; } = [];
        public Queue<PullResult> Pages { get; } = new();
        public int Pulls { get; private set; }
        public List<SyncCursor> Cursors { get; } = [];

        /// <summary>When set, every pull fails with this message (the master saying the replica must be rebuilt).</summary>
        public string? PullError { get; set; }

        private readonly List<ChangeEnvelope> _log = [];

        /// <summary>The master was restored from an older backup: it has none of what was pushed.</summary>
        public void ForgetLog() => _log.Clear();

        public event EventHandler<ConnectionState>? StateChanged;
#pragma warning disable CS0067 // The loop subscribes; these tests never raise it.
        public event EventHandler<Guid>? ChangesAvailable;
#pragma warning restore CS0067
        public event EventHandler<Guid>? EngagementLocked;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (FailConnect)
            {
                throw new HttpRequestException("No route to the master.");
            }

            State = ConnectionState.Online;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        /// <summary>The connection is lost: the hub forgets this client's groups.</summary>
        public void Drop()
        {
            Subscribed = [];
            State = ConnectionState.Offline;
            StateChanged?.Invoke(this, State);
        }

        public void RaiseLocked(Guid engagementId) => EngagementLocked?.Invoke(this, engagementId);

        public Task SubscribeAsync(Guid engagementId, CancellationToken cancellationToken)
        {
            Subscribed.Add(engagementId);
            return Task.CompletedTask;
        }

        public Task<PushResult> PushAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
        {
            Pushed.AddRange(changes);
            _log.AddRange(changes.Select(c => c with { Sequence = _log.Count + 1, ReceivedAt = DateTimeOffset.UnixEpoch }));
            return Task.FromResult(new PushResult([.. changes.Select(c => new ChangeResult(c.ChangeId, ChangeOutcome.Accepted))], SyncCursor.Start));
        }

        public Task<PullResult> PullAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
        {
            Pulls++;
            Cursors.Add(since);
            if (PullError is not null)
            {
                throw new InvalidOperationException(PullError);
            }

            if (Pages.Count > 0)
            {
                return Task.FromResult(Pages.Dequeue());
            }

            // Like the master: what was pushed comes back in the log.
            var page = _log.Where(c => c.Sequence > since.Sequence).Take(maxChanges).ToList();
            return Task.FromResult(new PullResult(page, page.Count > 0 ? new SyncCursor(page[^1].Sequence) : since, false));
        }
    }
}
