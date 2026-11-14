using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Tests;

public sealed class SyncAgentTests : IDisposable
{
    private static readonly Guid Eng = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly LocalDbContext _db;
    private readonly FakeTransport _transport = new();
    private readonly RecordingSink _sink = new();
    private readonly SyncAgent _agent;

    public SyncAgentTests()
    {
        _connection.Open();
        _db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Engagements.Add(new LocalEngagementRow { Id = Eng, Code = "ENG-1", ClientName = "Client A", Status = EngagementStatus.Fieldwork });
        _db.SaveChanges();
        _agent = new SyncAgent(_db, _transport, _sink, new ClientOptions { ClientId = Guid.NewGuid() }, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static ChangeEnvelope Change(int minute) =>
        new(Guid.NewGuid(), Eng, Guid.NewGuid(), Guid.NewGuid(), Guid.Empty,
            new DateTimeOffset(2026, 11, 9, 10, minute, 0, TimeSpan.Zero), "test-v1", [(byte)minute]);

    [Fact]
    public async Task Offline_edits_stay_in_the_outbox()
    {
        var ct = TestContext.Current.CancellationToken;
        _transport.State = ConnectionState.Offline;
        await _agent.EnqueueAsync(Change(1), ct);

        Assert.Equal(0, await _agent.PushPendingAsync(Eng, ct));
        Assert.Equal(1, await _db.Outbox.CountAsync(ct));
        Assert.Empty(_transport.Pushed);
    }

    [Fact]
    public async Task A_change_that_became_held_after_the_first_look_is_not_sent_before_its_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var free = Change(1);
        var link = Change(2);
        await _agent.EnqueueAsync(free, ct);
        await _agent.EnqueueAsync(link, ct);
        var linkSequence = (await _db.Outbox.SingleAsync(o => o.ChangeId == link.ChangeId, ct)).LocalSequence;

        // The link, with its file's row, is saved after the sender asked what to hold, and before it read the outbox:
        // the second answer has it, the first did not.
        var asked = 0;
        Task<IReadOnlyCollection<long>> Held(CancellationToken _) =>
            Task.FromResult<IReadOnlyCollection<long>>(asked++ == 0 ? [] : [linkSequence]);

        Assert.Equal(1, await _agent.PushPendingAsync(Eng, ct, Held));

        Assert.Equal([free.ChangeId], _transport.Pushed.Select(c => c.ChangeId));
        Assert.Equal(OutboxState.Pending, (await _db.Outbox.SingleAsync(o => o.ChangeId == link.ChangeId, ct)).State);
    }

    [Fact]
    public async Task Push_sends_in_the_order_the_changes_were_made_whatever_the_clock_says_and_settles_each_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        // The laptop clock went backwards between the saves; the order of the saves is what counts (ADR-0004).
        var first = Change(5);
        var second = Change(1);
        var conflicting = Change(3);
        var refused = Change(2);
        await _agent.EnqueueAsync(first, ct);
        await _agent.EnqueueAsync(second, ct);
        await _agent.EnqueueAsync(conflicting, ct);
        await _agent.EnqueueAsync(refused, ct);
        _transport.Outcomes[conflicting.ChangeId] = ChangeOutcome.Conflict;
        _transport.Outcomes[refused.ChangeId] = ChangeOutcome.Rejected;

        var delivered = await _agent.PushPendingAsync(Eng, ct);

        Assert.Equal(2, delivered);
        Assert.Equal([first.ChangeId, second.ChangeId, conflicting.ChangeId, refused.ChangeId], _transport.Pushed.Select(c => c.ChangeId));
        var remaining = await _db.Outbox.OrderBy(o => o.LocalSequence).ToListAsync(ct);
        // Accepted changes stay (pending) until the pull brings them into the replica; flagged and refused ones are kept with their state.
        Assert.Equal(
            [(first.ChangeId, OutboxState.Pending), (second.ChangeId, OutboxState.Pending),
                (conflicting.ChangeId, OutboxState.Conflict), (refused.ChangeId, OutboxState.Rejected)],
            remaining.Select(r => (r.ChangeId, r.State)));

        // Flagged and refused changes are never sent again; accepted ones that have not come back yet are redelivered (the master answers Duplicate).
        Assert.Equal(2, await _agent.PushPendingAsync(Eng, ct));
        Assert.Equal([first.ChangeId, second.ChangeId], _transport.Pushed.Skip(4).Select(c => c.ChangeId));
    }

    [Fact]
    public async Task A_page_that_cannot_be_applied_moves_neither_the_replica_nor_the_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        _transport.Pages.Enqueue(new PullResult([Change(1)], new SyncCursor(1), HasMore: true));
        _transport.Pages.Enqueue(new PullResult([Change(2)], new SyncCursor(2), HasMore: false));
        _sink.FailOnCall = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _agent.PullAsync(Eng, ct));

        Assert.Equal(1, (await _db.SyncState.AsNoTracking().SingleAsync(ct)).LastPulledSequence);
        // The next attempt asks for the failed page again.
        _transport.Pages.Enqueue(new PullResult([Change(2)], new SyncCursor(2), HasMore: false));
        _sink.FailOnCall = 0;
        Assert.Equal(1, await _agent.PullAsync(Eng, ct));
        Assert.Equal([0L, 1L, 1L], _transport.PulledFrom);
    }

    [Fact]
    public async Task Locked_engagement_refuses_local_edits()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = await _db.Engagements.SingleAsync(ct);
        row.IsReadOnly = true;
        await _db.SaveChangesAsync(ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _agent.EnqueueAsync(Change(1), ct));
    }

    [Fact]
    public async Task Pull_pages_until_done_and_persists_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        _transport.Pages.Enqueue(new PullResult([Change(1), Change(2)], new SyncCursor(2), HasMore: true));
        _transport.Pages.Enqueue(new PullResult([Change(3)], new SyncCursor(3), HasMore: false));

        Assert.Equal(3, await _agent.PullAsync(Eng, ct));
        Assert.Equal(3, _sink.Applied.Count);
        Assert.Equal(3, (await _db.SyncState.SingleAsync(ct)).LastPulledSequence);
        Assert.Equal([0L, 2L], _transport.PulledFrom);
    }

    private sealed class FakeTransport : ISyncTransport
    {
        public ConnectionState State { get; set; } = ConnectionState.Online;
        public List<ChangeEnvelope> Pushed { get; } = [];
        public Dictionary<Guid, ChangeOutcome> Outcomes { get; } = [];
        public Queue<PullResult> Pages { get; } = new();
        public List<long> PulledFrom { get; } = [];

        public event EventHandler<ConnectionState>? StateChanged { add { } remove { } }
        public event EventHandler<Guid>? ChangesAvailable { add { } remove { } }
        public event EventHandler<Guid>? EngagementLocked { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SubscribeAsync(Guid engagementId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PushResult> PushAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
        {
            Pushed.AddRange(changes);
            return Task.FromResult(new PushResult(
                [.. changes.Select(c => new ChangeResult(c.ChangeId, Outcomes.GetValueOrDefault(c.ChangeId, ChangeOutcome.Accepted)))],
                new SyncCursor(Pushed.Count)));
        }

        public Task<PullResult> PullAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
        {
            PulledFrom.Add(since.Sequence);
            return Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : new PullResult([], since, false));
        }
    }

    private sealed class RecordingSink : ILocalChangeSink
    {
        public List<ChangeEnvelope> Applied { get; } = [];
        public int FailOnCall { get; set; }
        private int _calls;

        public Task ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
        {
            if (++_calls == FailOnCall)
            {
                throw new InvalidOperationException("Replica cannot apply this page.");
            }

            Applied.AddRange(changes);
            return Task.CompletedTask;
        }
    }
}
