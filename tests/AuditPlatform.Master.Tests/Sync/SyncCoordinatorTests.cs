using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync;

namespace AuditPlatform.Master.Tests.Sync;

public class SyncCoordinatorTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Eng = Guid.NewGuid();

    private sealed class FakeAccess(EngagementAccess result) : IEngagementAccess
    {
        public ValueTask<EngagementAccess> GetAsync(Guid engagementId, Guid userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result);
    }

    /// <summary>Records what reaches the engine; accepts everything.</summary>
    private sealed class RecordingEngine : IDocumentSyncEngine
    {
        public List<ChangeEnvelope> Received { get; } = [];
        public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string> { "test-v1" };

        public ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
        {
            Received.AddRange(changes);
            return ValueTask.FromResult(new PushResult(
                [.. changes.Select(c => new ChangeResult(c.ChangeId, ChangeOutcome.Accepted))], new SyncCursor(Received.Count)));
        }

        public ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PullResult([], since, false));
    }

    private static ChangeEnvelope Change(string kind = "test-v1", Guid? engagement = null, Guid? author = null) =>
        new(Guid.NewGuid(), engagement ?? Eng, Guid.NewGuid(), author ?? User, Guid.NewGuid(), DateTimeOffset.UtcNow, kind, [1, 2, 3]);

    private static (SyncCoordinator Sut, RecordingEngine Engine, InMemoryAuditLog Log) Create(EngagementAccess access)
    {
        var engine = new RecordingEngine();
        var log = new InMemoryAuditLog();
        return (new SyncCoordinator(new FakeAccess(access), engine, log, TimeProvider.System), engine, log);
    }

    [Fact]
    public async Task Non_member_is_refused_without_revealing_whether_engagement_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var (notMember, _, _) = Create(new EngagementAccess(Exists: true, IsMember: false, IsReadOnly: false));
        var (missing, _, _) = Create(new EngagementAccess(Exists: false, IsMember: false, IsReadOnly: true));

        var a = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await notMember.PushAsync(User, Eng, [Change()], ct));
        var b = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await missing.PushAsync(User, Eng, [Change()], ct));
        Assert.Equal(a.Message, b.Message);
    }

    [Fact]
    public async Task Locked_engagement_rejects_every_change_and_engine_is_not_called()
    {
        var (sut, engine, log) = Create(new EngagementAccess(true, true, IsReadOnly: true));
        var result = await sut.PushAsync(User, Eng, [Change(), Change()], TestContext.Current.CancellationToken);

        Assert.All(result.Results, r => Assert.Equal(ChangeOutcome.Rejected, r.Outcome));
        Assert.Empty(engine.Received);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Author_is_taken_from_the_connection_not_the_payload()
    {
        var (sut, engine, _) = Create(new EngagementAccess(true, true, false));
        await sut.PushAsync(User, Eng, [Change(author: Guid.NewGuid())], TestContext.Current.CancellationToken);

        Assert.Equal(User, Assert.Single(engine.Received).AuthorUserId);
    }

    [Fact]
    public async Task Foreign_engagement_and_unknown_payload_kind_are_rejected_individually()
    {
        var (sut, engine, _) = Create(new EngagementAccess(true, true, false));
        var good = Change();
        var foreign = Change(engagement: Guid.NewGuid());
        var unknown = Change(kind: "crdt-v9");

        var result = await sut.PushAsync(User, Eng, [good, foreign, unknown], TestContext.Current.CancellationToken);

        Assert.Equal(good.ChangeId, Assert.Single(engine.Received).ChangeId);
        Assert.Equal(ChangeOutcome.Accepted, result.Results.Single(r => r.ChangeId == good.ChangeId).Outcome);
        Assert.Equal(ChangeOutcome.Rejected, result.Results.Single(r => r.ChangeId == foreign.ChangeId).Outcome);
        Assert.Equal(ChangeOutcome.Rejected, result.Results.Single(r => r.ChangeId == unknown.ChangeId).Outcome);
    }

    [Fact]
    public async Task Accepted_push_is_written_to_the_hash_chained_audit_log()
    {
        var (sut, _, log) = Create(new EngagementAccess(true, true, false));
        await sut.PushAsync(User, Eng, [Change(), Change()], TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("sync.push", entry.Action);
        Assert.Equal(User, entry.ActorUserId);
        Assert.Contains("\"accepted\":2", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Null(HashChain.Verify(log.Entries));
    }
}
