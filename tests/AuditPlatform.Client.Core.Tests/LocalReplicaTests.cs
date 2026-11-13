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
}
