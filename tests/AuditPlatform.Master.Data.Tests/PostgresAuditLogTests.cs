using AuditPlatform.AuditLog;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuditPlatform.Master.Data.Tests;

public sealed class PostgresAuditLogTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static AuditEvent Event(int i) =>
        new(DateTimeOffset.UtcNow, Guid.NewGuid(), "test.event", "test", i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, $$"""{"i":{{i}}}""");

    [Fact]
    public async Task Concurrent_appends_produce_one_unbroken_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        await Parallel.ForEachAsync(Enumerable.Range(0, 40), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
        {
            await using var db = pg.CreateContext();
            await new PostgresAuditLog(db).AppendAsync(Event(i), token);
        });

        await using var read = pg.CreateContext();
        var rows = await read.AuditLog.AsNoTracking().OrderBy(r => r.Sequence).ToListAsync(ct);
        var entries = rows.Select(r => new AuditEntry(r.Sequence, r.OccurredAt, r.ActorUserId, r.Action, r.EntityType, r.EntityId,
            r.EngagementId, r.PayloadJson, r.PreviousHash, r.Hash));

        Assert.True(rows.Count >= 40);
        Assert.Null(HashChain.Verify(entries));
    }

    [Theory]
    [InlineData("UPDATE audit_log SET action = 'tampered'")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("TRUNCATE audit_log")]
    public async Task Database_rejects_mutation_of_the_audit_log(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = pg.CreateContext();
        await new PostgresAuditLog(db).AppendAsync(Event(0), ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, ct));
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_journal_rejects_duplicate_change_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = pg.CreateContext();
        var engagement = new EngagementRow
        {
            Id = Guid.NewGuid(), Code = "ENG-" + Guid.NewGuid().ToString("N")[..6], ClientName = "C", ContractReference = "R",
            PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31),
        };
        db.Engagements.Add(engagement);
        var changeId = Guid.NewGuid();
        db.Changes.Add(NewChange(engagement.Id, 1, changeId));
        await db.SaveChangesAsync(ct);

        db.Changes.Add(NewChange(engagement.Id, 2, changeId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }

    private static ChangeRow NewChange(Guid engagementId, long sequence, Guid changeId) => new()
    {
        EngagementId = engagementId, Sequence = sequence, ChangeId = changeId, DocumentId = Guid.NewGuid(),
        AuthorUserId = Guid.NewGuid(), ClientId = Guid.NewGuid(), ClientCreatedAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow, PayloadKind = "test-v1", Payload = [1],
    };
}
