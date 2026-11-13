using AuditPlatform.Contracts.Sync;
using AuditPlatform.Sync.Spike.Crdt;
using AuditPlatform.Sync.Spike.FieldVersioning;

namespace AuditPlatform.Sync.Spike.Scenarios;

/// <summary>
/// S11 (ADR-0004 criterion 1): three laptops, three days, mostly offline, on a flaky line. The same scripted
/// workload is replayed against both candidates for 200 different seeds.
/// </summary>
public class ConvergenceScenarios
{
    private const int Seeds = 200;
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
                    steps.Add(new Sync(client, random.Next(4) == 0 ? (SyncFault)(1 + random.Next(2)) : SyncFault.None));
                }
            }
        }

        return steps;
    }

    [Fact]
    public async Task S11_field_versioning_converges_and_accounts_for_every_edit()
    {
        var edits = 0;
        var flagged = 0;
        for (var seed = 0; seed < Seeds; seed++)
        {
            var engagement = Guid.NewGuid();
            var paper = Guid.NewGuid();
            var master = new InMemoryFieldSyncEngine(TimeProvider.System);
            var clients = Enumerable.Range(0, Clients).Select(_ => new FieldClient(Guid.NewGuid(), engagement, TimeProvider.System)).ToArray();
            clients[0].CreatePaper(paper, "AUP-CSH-03", 1, clients[0].UserId);
            await SyncAll();

            var script = Script(seed);
            foreach (var step in script)
            {
                switch (step)
                {
                    case Edit e:
                        clients[e.Client].SetField(paper, e.Key, e.Value);
                        break;
                    case Sync s:
                        await clients[s.Client].SyncAsync(master, s.Fault);
                        break;
                }
            }

            await SyncAll();

            var onMaster = master.Document(engagement, paper);
            var issued = script.OfType<Edit>().Count();
            Assert.All(clients, c => Assert.Equal(onMaster.Fingerprint(), c.Confirmed(paper).Fingerprint()));
            Assert.All(clients, c => Assert.Equal(onMaster.ComputeContentHash(), c.Confirmed(paper).ComputeContentHash()));
            // Every edit is either in the field history or recorded as a flagged conflict. None vanished, none is still queued.
            Assert.Equal(issued, onMaster.History.Count + onMaster.Conflicts.Count);
            Assert.All(clients, c => Assert.DoesNotContain(c.Outbox, p => p.State != PendingState.Conflict));
            // Every conflict that is still open is on its author's list.
            Assert.Equal(onMaster.OpenConflicts.Count(), clients.Sum(c => c.NeedsAttention.Count()));
            edits += issued;
            // An author's later offline save of the same field replaces their earlier proposal; count each collision once.
            flagged += onMaster.Conflicts.Values.Count(c => c.Status != ConflictStatus.Superseded);

            async Task SyncAll()
            {
                for (var round = 0; round < 2; round++)
                {
                    foreach (var c in clients)
                    {
                        await c.SyncAsync(master);
                    }
                }
            }
        }

        // Fixed by the seeds. The workload is deliberately hostile (three people editing the same eight fields of one
        // paper while mostly offline), so collisions are far more frequent than in real fieldwork.
        Assert.Equal((21_600, ExpectedCollisions), (edits, flagged));
    }

    [Fact]
    public async Task S11_crdt_converges_and_reports_none_of_the_same_collisions()
    {
        var signals = 0;
        for (var seed = 0; seed < Seeds; seed++)
        {
            var engagement = Guid.NewGuid();
            var paper = Guid.NewGuid();
            using var master = new YrsSyncEngine();
            var clients = Enumerable.Range(0, Clients).Select(_ => new YrsClient(Guid.NewGuid(), engagement, paper)).ToArray();

            foreach (var step in Script(seed))
            {
                switch (step)
                {
                    case Edit e:
                        clients[e.Client].Paper.SetField(e.Key, e.Value);
                        break;
                    case Sync s:
                        await clients[s.Client].SyncAsync(master, deliverTwice: s.Fault != SyncFault.None);
                        break;
                }
            }

            for (var round = 0; round < 2; round++)
            {
                foreach (var c in clients)
                {
                    await c.SyncAsync(master);
                }
            }

            var expected = master.Document(paper, engagement).Fingerprint();
            Assert.All(clients, c => Assert.Equal(expected, c.Paper.Fingerprint()));
            signals += clients.Sum(c => c.PushResults.Count(r => r.Outcome is not (ChangeOutcome.Accepted or ChangeOutcome.Duplicate)));
            foreach (var c in clients)
            {
                c.Dispose();
            }
        }

        // The same workload in which candidate (a) surfaced 3,971 collisions: here, not one signal.
        Assert.Equal(0, signals);
    }

    private const int ExpectedCollisions = 3_971;
}
