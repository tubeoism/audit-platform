using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Tests;

/// <summary>The SQLite replica on its own: it follows the master's log exactly, or it stops.</summary>
public sealed class LocalReplicaTests : IDisposable
{
    private static readonly Guid Eng = Guid.NewGuid();
    private static readonly Guid Paper = Guid.NewGuid();
    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly LocalDbContext _db;
    private readonly LocalReplica _replica;
    private long _sequence;

    public LocalReplicaTests()
    {
        _connection.Open();
        _db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _replica = new LocalReplica(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ChangeEnvelope FromMaster(Guid author, FieldOp op, long? sequence = null) =>
        new(Guid.NewGuid(), Eng, Paper, author, Guid.NewGuid(), DateTimeOffset.UtcNow, FieldPayload.Kind, FieldPayload.Encode(op),
            sequence ?? ++_sequence, DateTimeOffset.UtcNow);

    private ChangeEnvelope FromClient(Guid client, Guid author, FieldOp op) =>
        FromMaster(author, op) with { ClientId = client };

    private static OutboxChangeRow Outbox(ChangeEnvelope change, OutboxState state = OutboxState.Pending, string? error = null) => new()
    {
        ChangeId = change.ChangeId, EngagementId = change.EngagementId, DocumentId = change.DocumentId, AuthorUserId = change.AuthorUserId,
        CreatedAt = change.CreatedAt, PayloadKind = change.PayloadKind, Payload = change.Payload, State = state, LastError = error,
    };

    private async Task<ChangeEnvelope[]> SeedAsync()
    {
        ChangeEnvelope[] log =
        [
            FromMaster(Preparer, new CreatePaper("AUP-CSH-03", 1, Preparer)),
            FromMaster(Preparer, new SetField("exceptions_noted", "0", null)),
            FromMaster(Preparer, new SubmitForReview()),
        ];
        await _replica.ApplyAsync(Eng, log, Ct);
        return log;
    }

    [Fact]
    public async Task Replica_stores_the_log_and_the_document_and_survives_a_restart()
    {
        var log = await SeedAsync();

        // A new context over the same database: what a restarted client sees.
        await using var reopened = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(_connection).Options);
        var replica = new LocalReplica(reopened);
        var doc = await replica.GetDocumentAsync(Eng, Paper, Ct);

        Assert.Equal(("0", log[1].ChangeId, Preparer), (doc.Value("exceptions_noted"), doc.Fields["exceptions_noted"].ChangeId, doc.Fields["exceptions_noted"].Author));
        Assert.Equal(doc.Fingerprint(), (await replica.ReplayDocumentAsync(Eng, Paper, Ct)).Fingerprint());
        Assert.Equal([1L, 2L, 3L], await reopened.ReplicaLog.OrderBy(r => r.Sequence).Select(r => r.Sequence).ToListAsync(Ct));
        var row = await reopened.Documents.SingleAsync(Ct);
        Assert.Equal(("AUP-CSH-03", "ReadyForReview", doc.ComputeContentHash(), 3L), (row.TemplateCode, row.Status, row.ContentHash, row.LastSequence));
    }

    [Fact]
    public async Task A_page_delivered_twice_is_applied_once()
    {
        var log = await SeedAsync();
        var before = (await _replica.GetDocumentAsync(Eng, Paper, Ct)).Fingerprint();

        await _replica.ApplyAsync(Eng, log, Ct);

        Assert.Equal(before, (await _replica.GetDocumentAsync(Eng, Paper, Ct)).Fingerprint());
        Assert.Equal(3, await _db.ReplicaLog.CountAsync(Ct));
    }

    [Fact]
    public async Task A_gap_in_the_log_stops_the_replica()
    {
        await SeedAsync();
        var skipped = FromMaster(Preparer, new SetField("sample_size", "25", null), sequence: 5);

        var ex = await Assert.ThrowsAsync<ReplicaDivergedException>(() => _replica.ApplyAsync(Eng, [skipped], Ct));
        Assert.Contains("does not follow local sequence 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_off_is_replayed_only_if_the_replica_computes_the_hash_the_master_sealed()
    {
        await SeedAsync();
        var doc = await _replica.GetDocumentAsync(Eng, Paper, Ct);

        // The master sealed content this replica does not have: the replica must not pretend it is signed.
        var wrong = FromMaster(Manager, new SignOffPaper(nameof(EngagementRole.ManagerQa), new string('a', 64)), sequence: 4);
        var ex = await Assert.ThrowsAsync<ReplicaDivergedException>(() => _replica.ApplyAsync(Eng, [wrong], Ct));
        Assert.Contains("hash does not match", ex.Message, StringComparison.Ordinal);
        _db.ChangeTracker.Clear();
        Assert.Empty((await _replica.GetDocumentAsync(Eng, Paper, Ct)).SignOffs);

        var right = FromMaster(Manager, new SignOffPaper(nameof(EngagementRole.ManagerQa), doc.ComputeContentHash()), sequence: 4);
        await _replica.ApplyAsync(Eng, [right], Ct);
        Assert.Equal(right.ChangeId, Assert.Single((await _replica.GetDocumentAsync(Eng, Paper, Ct)).SignOffs).ChangeId);
    }

    [Fact]
    public async Task The_log_settles_the_authors_own_pending_changes_when_the_masters_answer_was_lost()
    {
        var log = await SeedAsync();
        // Two local saves were pushed; the answers never arrived. Someone else had changed the field first.
        var theirs = FromMaster(Manager, new SetField("exceptions_noted", "1", log[1].ChangeId));
        var mineStale = FromMaster(Preparer, new SetField("exceptions_noted", "2", log[1].ChangeId));
        var mineFine = FromMaster(Preparer, new SetField("sample_size", "25", null));
        foreach (var mine in new[] { mineStale, mineFine })
        {
            _db.Outbox.Add(new OutboxChangeRow
            {
                ChangeId = mine.ChangeId, EngagementId = Eng, DocumentId = Paper, AuthorUserId = Preparer, CreatedAt = mine.CreatedAt,
                PayloadKind = mine.PayloadKind, Payload = mine.Payload, State = OutboxState.Pending,
            });
        }

        await _db.SaveChangesAsync(Ct);

        await _replica.ApplyAsync(Eng, [theirs, mineStale, mineFine], Ct);

        var left = Assert.Single(await _db.Outbox.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((mineStale.ChangeId, OutboxState.Conflict), (left.ChangeId, left.State));
        Assert.Contains("changed by someone else", left.LastError, StringComparison.Ordinal);

        // Once a person has resolved the conflict, it leaves the author's list.
        var resolved = FromMaster(Manager, new ResolveConflict(mineStale.ChangeId, "2", theirs.ChangeId));
        await _replica.ApplyAsync(Eng, [resolved], Ct);
        Assert.Empty(await _db.Outbox.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("2", (await _replica.GetDocumentAsync(Eng, Paper, Ct)).Value("exceptions_noted"));
    }

    [Fact]
    public async Task A_rebuild_empties_the_replica_and_resubmits_the_laptops_own_changes_before_the_ones_still_waiting()
    {
        var me = Guid.NewGuid();
        ChangeEnvelope[] log =
        [
            FromClient(me, Preparer, new CreatePaper("AUP-CSH-03", 1, Preparer)),
            FromClient(me, Preparer, new SetField("exceptions_noted", "0", null)),
            FromClient(Guid.NewGuid(), Manager, new SetField("sample_size", "25", null)),   // somebody else's laptop
        ];
        await _replica.ApplyAsync(Eng, log, Ct);
        _db.SyncState.Add(new SyncStateRow { EngagementId = Eng, LastPulledSequence = 3, LastSyncedAt = DateTimeOffset.UtcNow });
        var waiting = FromMaster(Preparer, new SetField("exceptions_noted", "1", log[1].ChangeId));
        var flagged = FromMaster(Preparer, new SetField("sample_size", "30", null));
        var refused = FromMaster(Preparer, new SetField("sample_size", "31", null));
        _db.Outbox.Add(Outbox(waiting));
        _db.Outbox.Add(Outbox(flagged, OutboxState.Conflict, "flagged"));
        _db.Outbox.Add(Outbox(refused, OutboxState.Rejected, "refused"));
        await _db.SaveChangesAsync(Ct);

        var report = await _replica.RebuildAsync(Eng, me, Preparer, Ct);

        Assert.Equal(new ReplicaRebuild(Resubmitted: 2, PendingKept: 1), report);
        Assert.Empty(await _db.ReplicaLog.ToListAsync(Ct));
        Assert.Empty(await _db.Documents.ToListAsync(Ct));
        var cursor = await _db.SyncState.SingleAsync(Ct);
        Assert.Equal((0L, null), (cursor.LastPulledSequence, cursor.LastSyncedAt));
        var pending = await _db.Outbox.Where(o => o.State == OutboxState.Pending).OrderBy(o => o.LocalSequence).Select(o => o.ChangeId).ToListAsync(Ct);
        Assert.Equal([log[0].ChangeId, log[1].ChangeId, waiting.ChangeId], pending);
        var kept = await _db.Outbox.Where(o => o.State != OutboxState.Pending).OrderBy(o => o.State).ToListAsync(Ct);
        Assert.Equal([(flagged.ChangeId, OutboxState.Conflict, "flagged"), (refused.ChangeId, OutboxState.Rejected, "refused")],
            kept.Select(o => (o.ChangeId, o.State, o.LastError)));
    }

    [Fact]
    public async Task A_rebuild_does_not_resubmit_what_is_still_in_the_outbox_and_leaves_other_engagements_alone()
    {
        var me = Guid.NewGuid();
        var mine = FromClient(me, Preparer, new CreatePaper("AUP-CSH-03", 1, Preparer));
        await _replica.ApplyAsync(Eng, [mine], Ct);
        _db.Outbox.Add(Outbox(mine));   // accepted by the master, waiting for the pull to settle it
        var other = Guid.NewGuid();
        _db.ReplicaLog.Add(new ReplicaChangeRow
        {
            EngagementId = other, Sequence = 1, ChangeId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), AuthorUserId = Preparer, ClientId = me,
            ClientCreatedAt = DateTimeOffset.UtcNow, PayloadKind = FieldPayload.Kind, Payload = [1],
        });
        _db.Documents.Add(new LocalDocumentRow { DocumentId = Guid.NewGuid(), EngagementId = other, TemplateCode = "X", Status = "Draft", ContentHash = "h", State = "{}" });
        _db.SyncState.Add(new SyncStateRow { EngagementId = other, LastPulledSequence = 1 });
        await _db.SaveChangesAsync(Ct);

        var report = await _replica.RebuildAsync(Eng, me, Preparer, Ct);

        Assert.Equal(new ReplicaRebuild(Resubmitted: 0, PendingKept: 1), report);
        Assert.Equal([mine.ChangeId], await _db.Outbox.Select(o => o.ChangeId).ToListAsync(Ct));
        Assert.Equal(1, await _db.ReplicaLog.CountAsync(r => r.EngagementId == other, Ct));
        Assert.Equal(1, await _db.Documents.CountAsync(d => d.EngagementId == other, Ct));
        Assert.Equal(1L, (await _db.SyncState.SingleAsync(s => s.EngagementId == other, Ct)).LastPulledSequence);
    }

    [Fact]
    public async Task A_rebuild_puts_back_only_the_changes_of_the_signed_in_user_not_those_of_another_person_on_the_same_laptop()
    {
        var me = Guid.NewGuid();
        var mine = FromClient(me, Preparer, new CreatePaper("AUP-CSH-03", 1, Preparer));
        var theirs = FromClient(me, Manager, new SetField("exceptions_noted", "0", null));   // the same laptop, another person, earlier
        await _replica.ApplyAsync(Eng, [mine, theirs], Ct);

        var report = await _replica.RebuildAsync(Eng, me, Preparer, Ct);

        // The master stamps a pushed change with the identity of the connection: sending the other person's work now
        // would make it the signed-in user's.
        Assert.Equal(new ReplicaRebuild(Resubmitted: 1, PendingKept: 0), report);
        Assert.Equal([mine.ChangeId], await _db.Outbox.Select(o => o.ChangeId).ToListAsync(Ct));
    }

    [Fact]
    public async Task A_rebuild_of_a_locked_engagement_puts_nothing_back_because_the_master_would_refuse_it_all()
    {
        var me = Guid.NewGuid();
        await _replica.ApplyAsync(Eng, [FromClient(me, Preparer, new CreatePaper("AUP-CSH-03", 1, Preparer))], Ct);
        _db.Engagements.Add(new LocalEngagementRow { Id = Eng, Code = "ENG-1", ClientName = "Client A", Status = EngagementStatus.Locked, IsReadOnly = true });
        await _db.SaveChangesAsync(Ct);

        var report = await _replica.RebuildAsync(Eng, me, Preparer, Ct);

        Assert.Equal(new ReplicaRebuild(Resubmitted: 0, PendingKept: 0), report);
        Assert.Empty(await _db.Outbox.ToListAsync(Ct));
        Assert.Empty(await _db.ReplicaLog.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_change_this_client_cannot_read_stops_the_replica_without_offering_a_rebuild()
    {
        await SeedAsync();
        var unreadable = FromMaster(Preparer, new SetField("sample_size", "25", null)) with { Payload = [0xFF, 0x00] };

        var ex = await Assert.ThrowsAsync<ReplicaDivergedException>(() => _replica.ApplyAsync(Eng, [unreadable], Ct));

        Assert.False(ex.RebuildHelps);
        Assert.Contains("cập nhật", ex.Message, StringComparison.Ordinal);
        Assert.True(new ReplicaDivergedException("gap").RebuildHelps);
    }
}
