using AuditPlatform.AuditLog;

namespace AuditPlatform.AuditLog.Tests;

public class HashChainTests
{
    private static readonly Guid Actor = Guid.NewGuid();

    private static async Task<List<AuditEntry>> BuildChainAsync(int count)
    {
        var log = new InMemoryAuditLog();
        for (var i = 0; i < count; i++)
        {
            await log.AppendAsync(new AuditEvent(
                new DateTimeOffset(2026, 11, 9, 8, i, 0, TimeSpan.FromHours(7)), Actor, "wp.field.set", "working_paper",
                $"wp-{i}", Guid.Empty, $$"""{"i":{{i}}}"""), TestContext.Current.CancellationToken);
        }

        return [.. log.Entries];
    }

    [Fact]
    public async Task Intact_chain_verifies()
    {
        var chain = await BuildChainAsync(5);
        Assert.Null(HashChain.Verify(chain));
        Assert.Equal(HashChain.GenesisHash, chain[0].PreviousHash);
        Assert.Equal(chain[0].Hash, chain[1].PreviousHash);
    }

    [Fact]
    public async Task Altered_payload_is_detected_at_that_entry()
    {
        var chain = await BuildChainAsync(5);
        chain[2] = chain[2] with { PayloadJson = """{"i":999}""" };

        var broken = HashChain.Verify(chain);
        Assert.NotNull(broken);
        Assert.Equal(3, broken.Sequence);
    }

    [Fact]
    public async Task Deleted_entry_is_detected()
    {
        var chain = await BuildChainAsync(5);
        chain.RemoveAt(1);

        var broken = HashChain.Verify(chain);
        Assert.NotNull(broken);
        Assert.Equal(3, broken.Sequence);
    }

    [Fact]
    public async Task Rehashed_forgery_is_detected_by_the_next_entry()
    {
        var chain = await BuildChainAsync(5);
        // Attacker edits entry 2 and recomputes its own hash, but cannot fix entry 3's previous-hash link.
        var forged = chain[1] with { Action = "wp.field.delete" };
        chain[1] = forged with { Hash = HashChain.ComputeHash(forged) };

        var broken = HashChain.Verify(chain);
        Assert.NotNull(broken);
        Assert.Equal(3, broken.Sequence);
    }

    [Fact]
    public void Hash_is_independent_of_input_offset()
    {
        var at = new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.FromHours(7));
        var a = HashChain.Seal(1, HashChain.GenesisHash, new AuditEvent(at, Actor, "x", "y", "z", null, "{}"));
        var b = HashChain.Seal(1, HashChain.GenesisHash, new AuditEvent(at.ToUniversalTime(), Actor, "x", "y", "z", null, "{}"));
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Timestamp_is_truncated_to_microseconds_so_database_round_trip_keeps_the_hash()
    {
        var at = new DateTimeOffset(638_000_000_000_000_007, TimeSpan.Zero); // 700 ns past a whole microsecond
        var entry = HashChain.Seal(1, HashChain.GenesisHash, new AuditEvent(at, Actor, "x", "y", "z", null, "{}"));
        var roundTripped = entry with { OccurredAt = new DateTimeOffset(at.UtcTicks / 10 * 10, TimeSpan.Zero) };

        Assert.Equal(0, entry.OccurredAt.UtcTicks % 10);
        Assert.Equal(entry.Hash, HashChain.ComputeHash(roundTripped));
    }
}
