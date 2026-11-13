using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync.Spike.FieldVersioning;

namespace AuditPlatform.Sync.Spike.Scenarios;

/// <summary>A laptop clock that is off by a fixed amount.</summary>
internal sealed class SkewedClock(TimeSpan offset) : TimeProvider
{
    private static readonly DateTimeOffset Base = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Base + offset;
}

/// <summary>Routes a simulated client through the master's real policy layer (membership, lock, author identity).</summary>
internal sealed class ViaCoordinator(SyncCoordinator coordinator, Guid userId) : IDocumentSyncEngine
{
    public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string>();

    public ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken) =>
        coordinator.PushAsync(userId, engagementId, changes, cancellationToken);

    public ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken) =>
        coordinator.PullAsync(userId, engagementId, since, maxChanges, cancellationToken);
}

internal sealed class SwitchableAccess : IEngagementAccess
{
    public bool Locked { get; set; }

    public ValueTask<EngagementAccess> GetAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EngagementAccess(Exists: true, IsMember: true, IsReadOnly: Locked));
}

/// <summary>
/// One engagement, one cash-count working paper (AUP-CSH-03), three people: the preparer in Hanoi, a second auditor
/// in Ho Chi Minh City and the reviewing manager. Everyone starts from the same synchronised state.
/// </summary>
internal sealed class FieldWorld
{
    public const string Conclusion = "conclusion";
    public const string Exceptions = "exceptions_noted";
    public const string SampleSize = "sample_size";

    public Guid Engagement { get; } = Guid.NewGuid();
    public Guid Paper { get; } = Guid.NewGuid();
    public InMemoryFieldSyncEngine Master { get; } = new(TimeProvider.System);
    public FieldClient Preparer { get; }
    public FieldClient Second { get; }
    public FieldClient Manager { get; }

    private FieldWorld(TimeProvider? secondClock)
    {
        Preparer = new FieldClient(Guid.NewGuid(), Engagement, TimeProvider.System);
        Second = new FieldClient(Guid.NewGuid(), Engagement, secondClock ?? TimeProvider.System);
        Manager = new FieldClient(Guid.NewGuid(), Engagement, TimeProvider.System);
    }

    public FieldDocument OnMaster => Master.Document(Engagement, Paper);

    public static async Task<FieldWorld> StartAsync(TimeProvider? secondClock = null)
    {
        var w = new FieldWorld(secondClock);
        w.Preparer.CreatePaper(w.Paper, "AUP-CSH-03", 1, w.Preparer.UserId);
        w.Preparer.SetField(w.Paper, SampleSize, "25");
        w.Preparer.SetField(w.Paper, Exceptions, "0");
        w.Preparer.SetField(w.Paper, Conclusion, "No exceptions noted in the sample of 25 items.");
        await w.SyncAllAsync();
        return w;
    }

    public async Task SyncAllAsync()
    {
        // Two rounds: the second lets everyone pull what the others pushed in the first.
        for (var round = 0; round < 2; round++)
        {
            await Preparer.SyncAsync(Master);
            await Second.SyncAsync(Master);
            await Manager.SyncAsync(Master);
        }
    }

    public void AssertConverged()
    {
        var expected = OnMaster.Fingerprint();
        Assert.Equal(expected, Preparer.Confirmed(Paper).Fingerprint());
        Assert.Equal(expected, Second.Confirmed(Paper).Fingerprint());
        Assert.Equal(expected, Manager.Confirmed(Paper).Fingerprint());
    }
}
