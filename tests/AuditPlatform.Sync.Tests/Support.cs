using AuditPlatform.AuditLog;
using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Data.Tests;
using AuditPlatform.Master.Notifications;
using AuditPlatform.Master.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Sync.Tests;

public enum MasterKind
{
    /// <summary>The reference model: the same reducer over an in-memory log. Runs everywhere, no database.</summary>
    Memory = 0,

    /// <summary>The durable engine on PostgreSQL (needs AUDIT_TEST_PG; skipped otherwise).</summary>
    Postgres = 1,
}

/// <summary>What the line does to one synchronisation, to model a flaky connection.</summary>
public enum SyncFault
{
    None = 0,

    /// <summary>The master applies the push but the answer never arrives; the pull that follows still works.</summary>
    LostAck = 1,

    /// <summary>The same batch is delivered twice in a row.</summary>
    DeliveredTwice = 2,

    /// <summary>The master applies the push, then the line drops: no answer and no pull.</summary>
    Dropped = 3,
}

/// <summary>A laptop clock that is off by a fixed amount.</summary>
internal sealed class SkewedClock(TimeSpan offset) : TimeProvider
{
    private static readonly DateTimeOffset Base = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Base + offset;
}

/// <summary>Every grant is good: these scenarios are about the sync rules. The real step-up is tested end to end in the master tests.</summary>
internal sealed class AlwaysSteppedUp : ISignOffStepUp
{
    public List<(Guid User, Guid WorkingPaper, string ContentHash)> Consumed { get; } = [];

    public Task<bool> ConsumeAsync(Guid userId, string? stepUpToken, Guid workingPaperId, string contentHash, CancellationToken cancellationToken)
    {
        Consumed.Add((userId, workingPaperId, contentHash));
        return Task.FromResult(true);
    }
}

/// <summary>The master as the scenarios see it: always through the real policy layer (membership, lock, author identity).</summary>
internal interface IMaster
{
    Task SeedAsync(Guid engagement, IReadOnlyList<(Guid User, EngagementRole Role)> members);
    Task<PushResult> PushAsync(Guid user, Guid engagement, IReadOnlyList<ChangeEnvelope> changes);
    Task<PullResult> PullAsync(Guid user, Guid engagement, SyncCursor since, int max);
    Task<SignOffAttempt> SignOffAsync(Guid engagement, Guid document, EngagementRole level, Guid reviewer);
    Task<FieldDocument> DocumentAsync(Guid engagement, Guid document);

    /// <summary>The document folded again from the stored log, to compare with the stored state.</summary>
    Task<FieldDocument> ReplayAsync(Guid engagement, Guid document);
    Task<long> HeadAsync(Guid engagement);
    Task LockAsync(Guid engagement);
}

internal sealed class MemoryMaster : IMaster
{
    private readonly ReferenceEngine _engine = new();
    private readonly SwitchableAccess _access = new();
    private readonly SyncCoordinator _coordinator;

    public MemoryMaster() => _coordinator = new SyncCoordinator(_access, _engine, AuditLog, TimeProvider.System);

    public InMemoryAuditLog AuditLog { get; } = new();

    public Task SeedAsync(Guid engagement, IReadOnlyList<(Guid User, EngagementRole Role)> members)
    {
        foreach (var group in members.GroupBy(m => m.User))
        {
            _engine.Roles[group.Key] = [.. group.Select(m => m.Role)];
        }

        return Task.CompletedTask;
    }

    public async Task<PushResult> PushAsync(Guid user, Guid engagement, IReadOnlyList<ChangeEnvelope> changes) =>
        await _coordinator.PushAsync(user, engagement, changes, TestContext.Current.CancellationToken);

    public async Task<PullResult> PullAsync(Guid user, Guid engagement, SyncCursor since, int max) =>
        await _coordinator.PullAsync(user, engagement, since, max, TestContext.Current.CancellationToken);

    public Task<SignOffAttempt> SignOffAsync(Guid engagement, Guid document, EngagementRole level, Guid reviewer) =>
        Task.FromResult(_engine.SignOff(engagement, document, level, reviewer));

    public Task<FieldDocument> DocumentAsync(Guid engagement, Guid document) => Task.FromResult(_engine.Document(engagement, document));

    public Task<FieldDocument> ReplayAsync(Guid engagement, Guid document) => Task.FromResult(_engine.Replay(engagement, document));

    public Task<long> HeadAsync(Guid engagement) => Task.FromResult(_engine.Head(engagement));

    public Task LockAsync(Guid engagement)
    {
        _access.Locked = true;
        return Task.CompletedTask;
    }

    private sealed class SwitchableAccess : IEngagementAccess
    {
        public bool Locked { get; set; }

        public ValueTask<EngagementAccess> GetAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EngagementAccess(Exists: true, IsMember: true, IsReadOnly: Locked));
    }
}

/// <summary>
/// The durable engine exactly as the master host uses it: a fresh DbContext, engine and coordinator per call (the hub
/// creates a scope per invocation), real membership rows, and the engagement lock as a status in the database.
/// </summary>
internal sealed class PostgresMaster(PostgresFixture pg) : IMaster
{
    public AlwaysSteppedUp StepUp { get; } = new();
    public NotificationSignal Signal { get; } = new();

    public MasterDbContext CreateContext() => pg.CreateContext();

    public async Task SeedAsync(Guid engagement, IReadOnlyList<(Guid User, EngagementRole Role)> members)
    {
        await using var db = pg.CreateContext();
        foreach (var user in members.Select(m => m.User).Distinct())
        {
            db.Users.Add(new UserRow { Id = user, Email = $"{user:N}@audit.io.vn", DisplayName = "Auditor " + user.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
        }

        db.Engagements.Add(new EngagementRow
        {
            Id = engagement, Code = "ENG-" + engagement.ToString("N")[..10], ClientName = "Client", ContractReference = "AUP-2026",
            PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31), Status = EngagementStatus.Fieldwork,
            Members = [.. members.Select(m => new EngagementMemberRow { EngagementId = engagement, UserId = m.User, Role = m.Role })],
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async Task<PushResult> PushAsync(Guid user, Guid engagement, IReadOnlyList<ChangeEnvelope> changes)
    {
        await using var db = pg.CreateContext();
        return await Coordinator(db).PushAsync(user, engagement, changes, TestContext.Current.CancellationToken);
    }

    public async Task<PullResult> PullAsync(Guid user, Guid engagement, SyncCursor since, int max)
    {
        await using var db = pg.CreateContext();
        return await Coordinator(db).PullAsync(user, engagement, since, max, TestContext.Current.CancellationToken);
    }

    public async Task<SignOffAttempt> SignOffAsync(Guid engagement, Guid document, EngagementRole level, Guid reviewer)
    {
        await using var db = pg.CreateContext();
        return await Engine(db).SignOffAsync(engagement, document, level, reviewer, "grant", StepUp, TestContext.Current.CancellationToken);
    }

    public async Task<FieldDocument> DocumentAsync(Guid engagement, Guid document)
    {
        await using var db = pg.CreateContext();
        return await Engine(db).GetDocumentAsync(engagement, document, TestContext.Current.CancellationToken) ?? new FieldDocument(document, engagement);
    }

    public async Task<FieldDocument> ReplayAsync(Guid engagement, Guid document)
    {
        await using var db = pg.CreateContext();
        return await Engine(db).ReplayDocumentAsync(engagement, document, TestContext.Current.CancellationToken);
    }

    public async Task<long> HeadAsync(Guid engagement)
    {
        await using var db = pg.CreateContext();
        return await Engine(db).HeadAsync(engagement, TestContext.Current.CancellationToken);
    }

    public async Task LockAsync(Guid engagement)
    {
        await using var db = pg.CreateContext();
        await db.Engagements.Where(e => e.Id == engagement)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EngagementStatus.Locked), TestContext.Current.CancellationToken);
    }

    public FieldSyncEngine Engine(MasterDbContext db) => new(db, new PostgresAuditLog(db), TimeProvider.System, Signal);

    private SyncCoordinator Coordinator(MasterDbContext db) =>
        new(new EfEngagementAccess(db), Engine(db), new PostgresAuditLog(db), TimeProvider.System);
}

/// <summary>Carries the laptop's calls straight to the master, with the faults of a flaky line.</summary>
internal sealed class DirectTransport(IMaster master, Guid userId) : ISyncTransport
{
    public SyncFault Fault { get; set; }
    public ConnectionState State { get; set; } = ConnectionState.Online;

    public event EventHandler<ConnectionState>? StateChanged { add { } remove { } }
    public event EventHandler<Guid>? ChangesAvailable { add { } remove { } }
    public event EventHandler<Guid>? EngagementLocked { add { } remove { } }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SubscribeAsync(Guid engagementId, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<PushResult> PushAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        var result = await master.PushAsync(userId, engagementId, changes);
        return Fault switch
        {
            SyncFault.DeliveredTwice => await master.PushAsync(userId, engagementId, changes),
            SyncFault.LostAck or SyncFault.Dropped => throw new IOException("The connection was lost before the master's answer arrived."),
            _ => result,
        };
    }

    public Task<PullResult> PullAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken) =>
        master.PullAsync(userId, engagementId, since, maxChanges);
}

/// <summary>One auditor's laptop: a real SQLite replica (in memory), the real sync agent and workspace.</summary>
internal sealed class Laptop : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _engagement;

    public Laptop(IMaster master, Guid engagement, Guid? userId = null, TimeProvider? clock = null)
    {
        _engagement = engagement;
        UserId = userId ?? Guid.NewGuid();
        _connection.Open();
        Db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();
        Db.Engagements.Add(new LocalEngagementRow { Id = engagement, Code = "ENG-1", ClientName = "Client", Status = EngagementStatus.Fieldwork });
        Db.SaveChanges();

        var options = new ClientOptions { ClientId = Guid.NewGuid() };
        Transport = new DirectTransport(master, UserId);
        Replica = new LocalReplica(Db);
        Replica.SealBroken += (_, e) => SealBreaks.Add(e);
        Agent = new SyncAgent(Db, Transport, Replica, options, clock ?? TimeProvider.System);
        Work = new EngagementWorkspace(Db, Agent, Replica, options, clock ?? TimeProvider.System, engagement, UserId);
    }

    public Guid UserId { get; }
    public LocalDbContext Db { get; }
    public DirectTransport Transport { get; }
    public LocalReplica Replica { get; }
    public SyncAgent Agent { get; }
    public EngagementWorkspace Work { get; }
    public List<SealBrokenEventArgs> SealBreaks { get; } = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<Guid> SetAsync(Guid paper, string key, string value) => Work.SetFieldAsync(paper, key, value, Ct);

    public Task<FieldDocument> ConfirmedAsync(Guid paper) => Work.ConfirmedAsync(paper, Ct);

    public Task<List<OutboxChangeRow>> NeedsAttentionAsync() => Work.NeedsAttentionAsync(Ct);

    public Task<List<OutboxChangeRow>> OutboxAsync() => Db.Outbox.AsNoTracking().OrderBy(o => o.LocalSequence).ToListAsync(Ct);

    public async Task SyncAsync(SyncFault fault = SyncFault.None)
    {
        Transport.Fault = fault;
        try
        {
            await Agent.PushPendingAsync(_engagement, Ct);
        }
        catch (IOException) when (fault is SyncFault.LostAck or SyncFault.Dropped)
        {
            // The laptop never learns what the master answered.
        }
        finally
        {
            Transport.Fault = SyncFault.None;
        }

        if (fault != SyncFault.Dropped)
        {
            await Agent.PullAsync(_engagement, Ct);
        }
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// One engagement, one cash-count working paper (AUP-CSH-03), three people: the preparer in Hanoi, a second auditor
/// in Ho Chi Minh City and the reviewing manager. Everyone starts from the same synchronised state.
/// </summary>
internal sealed class FieldWorld : IDisposable
{
    public const string Conclusion = "conclusion";
    public const string Exceptions = "exceptions_noted";
    public const string SampleSize = "sample_size";

    private FieldWorld(IMaster master, TimeProvider? secondClock)
    {
        Master = master;
        Preparer = new Laptop(master, Engagement);
        Second = new Laptop(master, Engagement, clock: secondClock);
        Manager = new Laptop(master, Engagement);
    }

    public Guid Engagement { get; } = Guid.NewGuid();
    public Guid Paper { get; } = Guid.NewGuid();
    public IMaster Master { get; }
    public Laptop Preparer { get; }
    public Laptop Second { get; }
    public Laptop Manager { get; }
    public IEnumerable<Laptop> Laptops => [Preparer, Second, Manager];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IMaster CreateMaster(MasterKind kind, PostgresFixture pg) => kind == MasterKind.Memory ? new MemoryMaster() : new PostgresMaster(pg);

    public static async Task<FieldWorld> StartAsync(MasterKind kind, PostgresFixture pg, TimeProvider? secondClock = null)
    {
        var w = new FieldWorld(CreateMaster(kind, pg), secondClock);
        await w.Master.SeedAsync(w.Engagement,
        [
            (w.Preparer.UserId, EngagementRole.Preparer), (w.Second.UserId, EngagementRole.Preparer), (w.Manager.UserId, EngagementRole.ManagerQa),
        ]);
        await w.Preparer.Work.CreatePaperAsync(w.Paper, "AUP-CSH-03", 1, Ct);
        await w.Preparer.SetAsync(w.Paper, SampleSize, "25");
        await w.Preparer.SetAsync(w.Paper, Exceptions, "0");
        await w.Preparer.SetAsync(w.Paper, Conclusion, "No exceptions noted in the sample of 25 items.");
        await w.SyncAllAsync();
        return w;
    }

    public Task<FieldDocument> OnMasterAsync() => Master.DocumentAsync(Engagement, Paper);

    public Task<SignOffAttempt> SignOffAsync(EngagementRole level = EngagementRole.ManagerQa) =>
        Master.SignOffAsync(Engagement, Paper, level, Manager.UserId);

    public async Task SyncAllAsync()
    {
        // Two rounds: the second lets everyone pull what the others pushed in the first.
        for (var round = 0; round < 2; round++)
        {
            foreach (var laptop in Laptops)
            {
                await laptop.SyncAsync();
            }
        }
    }

    /// <summary>
    /// Master and every laptop hold the same working paper, and on each of them the stored document is exactly the
    /// fold of the stored log.
    /// </summary>
    public async Task AssertConvergedAsync()
    {
        var expected = (await OnMasterAsync()).Fingerprint();
        Assert.Equal(expected, (await Master.ReplayAsync(Engagement, Paper)).Fingerprint());
        foreach (var laptop in Laptops)
        {
            Assert.Equal(expected, (await laptop.ConfirmedAsync(Paper)).Fingerprint());
            Assert.Equal(expected, (await laptop.Replica.ReplayDocumentAsync(Engagement, Paper, Ct)).Fingerprint());
        }
    }

    public void Dispose()
    {
        foreach (var laptop in Laptops)
        {
            laptop.Dispose();
        }
    }
}
