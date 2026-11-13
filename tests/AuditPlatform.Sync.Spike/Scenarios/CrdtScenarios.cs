using System.Text;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync.Spike.Crdt;

namespace AuditPlatform.Sync.Spike.Scenarios;

/// <summary>
/// Candidate (b), a CRDT (Yrs through YDotNet), against the same scenarios. These tests pin down what the library
/// actually does; where that differs from what ADR-0004 requires, the test name says so.
/// </summary>
public sealed class CrdtScenarios : IDisposable
{
    private const string Exceptions = "exceptions_noted";
    private const string SampleSize = "sample_size";
    private const string Original = "No exceptions noted in the sample of 25 items.";

    private readonly Guid _engagement = Guid.NewGuid();
    private readonly Guid _paper = Guid.NewGuid();
    private readonly YrsSyncEngine _master = new();
    private readonly YrsClient _preparer;
    private readonly YrsClient _second;
    private readonly YrsClient _manager;

    public CrdtScenarios()
    {
        _preparer = new YrsClient(Guid.NewGuid(), _engagement, _paper);
        _second = new YrsClient(Guid.NewGuid(), _engagement, _paper);
        _manager = new YrsClient(Guid.NewGuid(), _engagement, _paper);
    }

    private YrsPaper OnMaster => _master.Document(_paper, _engagement);

    public void Dispose()
    {
        _preparer.Dispose();
        _second.Dispose();
        _manager.Dispose();
        _master.Dispose();
    }

    private async Task StartAsync()
    {
        _preparer.Paper.SetField(SampleSize, "25");
        _preparer.Paper.SetField(Exceptions, "0");
        _preparer.Paper.InsertConclusion(0, Original);
        await SyncAllAsync();
    }

    private async Task SyncAllAsync()
    {
        for (var round = 0; round < 2; round++)
        {
            await _preparer.SyncAsync(_master);
            await _second.SyncAsync(_master);
            await _manager.SyncAsync(_master);
        }
    }

    private void AssertConverged()
    {
        var expected = OnMaster.Fingerprint();
        Assert.Equal(expected, _preparer.Paper.Fingerprint());
        Assert.Equal(expected, _second.Paper.Fingerprint());
        Assert.Equal(expected, _manager.Paper.Fingerprint());
    }

    [Fact]
    public async Task S1_edits_to_different_fields_are_both_kept()
    {
        await StartAsync();

        _preparer.Paper.SetField(SampleSize, "30");
        _second.Paper.SetField(Exceptions, "2");
        await SyncAllAsync();

        Assert.Equal("30", OnMaster.Field(SampleSize));
        Assert.Equal("2", OnMaster.Field(Exceptions));
        AssertConverged();
    }

    [Fact]
    public async Task S2_same_field_one_value_is_discarded_and_nobody_is_told()
    {
        await StartAsync();

        _preparer.Paper.SetField(Exceptions, "2");
        _second.Paper.SetField(Exceptions, "3");
        await SyncAllAsync();

        AssertConverged();
        var kept = OnMaster.Field(Exceptions);
        Assert.True(kept is "2" or "3");
        // Both pushes were plain "Accepted": the protocol has no way to say that one auditor's figure was dropped.
        Assert.All(_preparer.PushResults.Concat(_second.PushResults), r => Assert.Equal(ChangeOutcome.Accepted, r.Outcome));
        // And the discarded figure is no longer anywhere in the document state a client can read.
        Assert.DoesNotContain(kept == "2" ? "3" : "2", OnMaster.Fields().Values);
    }

    [Fact]
    public async Task S3_concurrent_edits_of_the_conclusion_are_blended_into_a_sentence_nobody_wrote()
    {
        await StartAsync();

        // The preparer found two exceptions and corrects the first word.
        _preparer.Paper.RemoveConclusion(0, 2);
        _preparer.Paper.InsertConclusion(0, "Two");
        // The second auditor, still looking at "No exceptions", adds the overall conclusion.
        _second.Paper.InsertConclusion(Original.Length, " Control operates effectively.");
        await SyncAllAsync();

        AssertConverged();
        Assert.Equal("Two exceptions noted in the sample of 25 items. Control operates effectively.", OnMaster.Conclusion());
        Assert.All(_preparer.PushResults.Concat(_second.PushResults), r => Assert.Equal(ChangeOutcome.Accepted, r.Outcome));
    }

    [Fact]
    public async Task S5_a_note_ends_up_cleared_over_a_response_the_reviewer_never_read()
    {
        await StartAsync();
        var note = Guid.NewGuid();
        _manager.Paper.SetNote(note, "text", "Why 25?");
        _manager.Paper.SetNote(note, "status", "Open");
        await SyncAllAsync();
        _preparer.Paper.SetNote(note, "response", "Population is 180; 25 per the sampling table.");
        _preparer.Paper.SetNote(note, "status", "Responded");
        await SyncAllAsync();

        _manager.Paper.SetNote(note, "status", "Cleared");
        _preparer.Paper.SetNote(note, "response", "Correction: population is 310, sample should have been 30.");
        await SyncAllAsync();

        AssertConverged();
        Assert.Equal("Cleared", OnMaster.Note(note, "status"));
        Assert.StartsWith("Correction", OnMaster.Note(note, "response"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S6_evidence_links_converge()
    {
        await StartAsync();
        _preparer.Paper.LinkEvidence("hash-count-sheet");
        await SyncAllAsync();

        _preparer.Paper.LinkEvidence("hash-bank-confirmation");
        _second.Paper.UnlinkEvidence("hash-count-sheet");
        _second.Paper.LinkEvidence("hash-bank-confirmation");
        await SyncAllAsync();

        Assert.Equal(["hash-bank-confirmation"], OnMaster.Evidence());
        AssertConverged();
    }

    [Fact]
    public async Task S7_content_hash_is_reproducible_and_a_later_edit_is_detectable_by_comparing_hashes()
    {
        await StartAsync();
        var signedHash = OnMaster.ComputeContentHash();
        Assert.Equal(signedHash, _second.Paper.ComputeContentHash());

        _preparer.Paper.SetField(Exceptions, "1");
        await SyncAllAsync();

        // The master can see that the seal broke, but only as "this binary update changed the hash".
        Assert.NotEqual(signedHash, OnMaster.ComputeContentHash());
        AssertConverged();
    }

    [Fact]
    public async Task S7_stale_offline_edit_arriving_after_sign_off_silently_replaces_signed_content()
    {
        await StartAsync();

        _second.Paper.SetField(SampleSize, "40");
        _preparer.Paper.SetField(SampleSize, "30");
        await _preparer.SyncAsync(_master);
        var signedHash = OnMaster.ComputeContentHash();
        var signedValue = OnMaster.Field(SampleSize);

        await SyncAllAsync();

        // Whether the stale value replaces the signed one depends on Yrs' internal client ids, not on anything a
        // reviewer controls. Either way there is no flag; when it does replace, the seal breaks without a conflict.
        var replaced = OnMaster.Field(SampleSize) != signedValue;
        Assert.Equal(replaced, OnMaster.ComputeContentHash() != signedHash);
        Assert.All(_second.PushResults, r => Assert.Equal(ChangeOutcome.Accepted, r.Outcome));
        TestContext.Current.TestOutputHelper?.WriteLine($"stale edit replaced signed value: {replaced}");
    }

    [Fact]
    public async Task S8_after_a_rejected_push_the_laptop_replica_differs_from_the_master_for_good()
    {
        await StartAsync();
        var access = new SwitchableAccess();
        var coordinator = new SyncCoordinator(access, _master, new InMemoryAuditLog(), TimeProvider.System);
        var line = new ViaCoordinator(coordinator, _second.UserId);

        _second.Paper.SetField(Exceptions, "5");
        access.Locked = true;
        await _second.SyncAsync(line);

        Assert.Equal(ChangeOutcome.Rejected, Assert.Single(_second.PushResults, r => r.Outcome != ChangeOutcome.Accepted).Outcome);
        Assert.Equal("0", OnMaster.Field(Exceptions));
        // The edit is merged into the local document and cannot be taken out again; the only way back to the
        // archived state is to throw the local replica away and download the master's.
        Assert.Equal("5", _second.Paper.Field(Exceptions));
        Assert.NotEqual(OnMaster.Fingerprint(), _second.Paper.Fingerprint());
    }

    [Fact]
    public async Task S9_redelivery_changes_nothing()
    {
        await StartAsync();

        _preparer.Paper.SetField(Exceptions, "1");
        await _preparer.SyncAsync(_master, deliverTwice: true);
        await SyncAllAsync();

        Assert.Equal("1", OnMaster.Field(Exceptions));
        AssertConverged();
    }

    [Fact]
    public async Task Trail_offline_intermediate_values_and_the_author_are_not_in_what_the_master_receives()
    {
        await StartAsync();
        var before = _preparer.Paper.StateVector();

        _preparer.Paper.SetField(Exceptions, "intermediate-figure-7");
        _preparer.Paper.SetField(Exceptions, "final-figure-4");
        var update = _preparer.Paper.UpdateSince(before);
        var text = Encoding.UTF8.GetString(update);

        Assert.Contains("final-figure-4", text, StringComparison.Ordinal);
        // Yrs garbage-collects overwritten content, so the earlier save never reaches the master...
        Assert.DoesNotContain("intermediate-figure-7", text, StringComparison.Ordinal);
        // ...and the update identifies its origin by a random per-replica number, not by the signed-in user.
        Assert.DoesNotContain(_preparer.UserId.ToString("N"), text, StringComparison.Ordinal);
        Assert.DoesNotContain(_preparer.UserId.ToString(), text, StringComparison.Ordinal);
    }
}
