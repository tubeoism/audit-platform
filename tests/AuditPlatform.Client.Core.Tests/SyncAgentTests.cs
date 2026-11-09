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
    public async Task Push_sends_in_creation_order_and_settles_each_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var late = Change(5);
        var early = Change(1);
        var conflicting = Change(3);
        await _agent.EnqueueAsync(late, ct);
        await _agent.EnqueueAsync(early, ct);
        await _agent.EnqueueAsync(conflicting, ct);
        _transport.Outcomes[conflicting.ChangeId] = ChangeOutcome.Conflict;

        var delivered = await _agent.PushPendingAsync(Eng, ct);

        Assert.Equal(2, delivered);
        Assert.Equal([early.ChangeId, conflicting.ChangeId, late.ChangeId], _transport.Pushed.Select(c => c.ChangeId));
        var remaining = Assert.Single(await _db.Outbox.ToListAsync(ct));
        Assert.Equal(conflicting.ChangeId, remaining.ChangeId);
        Assert.Equal(OutboxState.Conflict, remaining.State);
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

        public Task ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
        {
            Applied.AddRange(changes);
            return Task.CompletedTask;
        }
    }
}
