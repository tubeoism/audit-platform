using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Data.Tests;

namespace AuditPlatform.Sync.Tests;

/// <summary>
/// S11 (ADR-0004 criterion 1): three laptops, three days, mostly offline, on a flaky line. The same scripted
/// workload as the spike, now through SQLite replicas and the sync agent, against the reference model and against
/// the durable PostgreSQL engine.
/// </summary>
public sealed class ConvergenceScenarios(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const int Clients = 3;
    private const int Days = 3;
    private const int EditsPerClientPerDay = 12;
    private static readonly string[] Keys = ["sample_size", "exceptions_noted", "population", "period", "result", "conclusion", "threshold", "preparer_note"];

    private abstract record Step(int Client);

    private sealed record Edit(int Client, string Key, string Value) : Step(Client);

    private sealed record Sync(int Client, SyncFault Fault) : Step(Client);

    /// <summary>A laptop syncs after roughly one edit in six, so most edits are made on stale data; one sync in four hits a fault.</summary>
    private static List<Step> Script(int seed)
    {
        var random = new Random(seed);
        var steps = new List<Step>();
        for (var day = 1; day <= Days; day++)
        {
            for (var slot = 0; slot < EditsPerClientPerDay * Clients; slot++)
            {
                var client = random.Next(Clients);
                steps.Add(new Edit(client, Keys[random.Next(Keys.Length)], $"d{day}-c{client}-{slot}"));
                if (random.Next(6) == 0)
                {
                    steps.Add(new Sync(client, random.Next(4) == 0 ? (SyncFault)(1 + random.Next(3)) : SyncFault.None));
                }
            }
        }

        return steps;
    }

    private sealed record Run(int Edits, int Collisions, string Fingerprint);

    private static async Task<Run> PlayAsync(IMaster master, int seed)
    {
        var ct = TestContext.Current.CancellationToken;
        // Fixed ids per seed, so two masters given the same script can be compared change for change.
        var ids = new Random(seed * 7919);
        Guid NextId()
        {
            var bytes = new byte[16];
            ids.NextBytes(bytes);
            return new Guid(bytes);
        }

        var engagement = Guid.NewGuid();
        var paper = NextId();
        var laptops = Enumerable.Range(0, Clients).Select(_ => new Laptop(master, engagement, NextId())).ToArray();
        try
        {
            await master.SeedAsync(engagement, [.. laptops.Select(l => (l.UserId, EngagementRole.Preparer))]);
            await laptops[0].Work.CreatePaperAsync(paper, "AUP-CSH-03", 1, ct);
            await SyncAll();

            var script = Script(seed);
            foreach (var step in script)
            {
                switch (step)
                {
                    case Edit e:
                        await laptops[e.Client].SetAsync(paper, e.Key, e.Value);
                        break;
                    case Sync s:
                        await laptops[s.Client].SyncAsync(s.Fault);
                        break;
                }
            }

            await SyncAll();

            var onMaster = await master.DocumentAsync(engagement, paper);
            var issued = script.OfType<Edit>().Count();
            Assert.Equal(onMaster.Fingerprint(), (await master.ReplayAsync(engagement, paper)).Fingerprint());
            var open = 0;
            foreach (var laptop in laptops)
            {
                var confirmed = await laptop.ConfirmedAsync(paper);
                Assert.Equal(onMaster.Fingerprint(), confirmed.Fingerprint());
                Assert.Equal(onMaster.ComputeContentHash(), confirmed.ComputeContentHash());
                var outbox = await laptop.OutboxAsync();
                // Nothing is still queued; what remains is flagged conflicts waiting for a person.
                Assert.DoesNotContain(outbox, o => o.State != OutboxState.Conflict);
                open += outbox.Count;
            }

            // Every edit is either in the field history or recorded as a flagged conflict. None vanished.
            Assert.Equal(issued, onMaster.History.Count + onMaster.Conflicts.Count);
            // Every conflict that is still open is on its author's list.
            Assert.Equal(onMaster.OpenConflicts.Count(), open);

            // Field values only: change ids differ between runs, values and their authors do not.
            var content = string.Join('\n', onMaster.Fields.Select(f => $"{f.Key}={f.Value.Value}@{f.Value.Author:N}"))
                + "\n" + string.Join('\n', onMaster.Conflicts.Values.Select(c => $"{c.Key}:{c.ProposedValue}:{c.Status}").Order(StringComparer.Ordinal));
            // An author's later offline save of the same field replaces their earlier proposal; count each collision once.
            return new Run(issued, onMaster.Conflicts.Values.Count(c => c.Status != ConflictStatus.Superseded), content);
        }
        finally
        {
            foreach (var laptop in laptops)
            {
                laptop.Dispose();
            }
        }

        async Task SyncAll()
        {
            for (var round = 0; round < 2; round++)
            {
                foreach (var laptop in laptops)
                {
                    await laptop.SyncAsync();
                }
            }
        }
    }

    [Fact]
    public async Task S11_reference_model_converges_and_accounts_for_every_edit_over_200_seeds()
    {
        var edits = 0;
        var collisions = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var run = await PlayAsync(new MemoryMaster(), seed);
            edits += run.Edits;
            collisions += run.Collisions;
        }

        // Fixed by the seeds. The workload is deliberately hostile (three people editing the same eight fields of one
        // paper while mostly offline), so collisions are far more frequent than in real fieldwork.
        Assert.Equal((21_600, ExpectedCollisions), (edits, collisions));
    }

    [Fact]
    public async Task S11_durable_engine_converges_and_decides_exactly_like_the_reference_model()
    {
        var edits = 0;
        var collisions = 0;
        for (var seed = 0; seed < PostgresSeeds; seed++)
        {
            var durable = await PlayAsync(new PostgresMaster(pg), seed);
            var reference = await PlayAsync(new MemoryMaster(), seed);
            Assert.Equal(reference, durable);
            edits += durable.Edits;
            collisions += durable.Collisions;
        }

        Assert.Equal(PostgresSeeds * Days * EditsPerClientPerDay * Clients, edits);
        Assert.True(collisions > 0);
    }

    private const int PostgresSeeds = 200;
    private const int ExpectedCollisions = 3_988;
}
