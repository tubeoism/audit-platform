using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync.Spike.Crdt;
using AuditPlatform.Sync.Spike.FieldVersioning;

namespace AuditPlatform.Sync.Spike.Scenarios;

/// <summary>Candidate (a), field-level versioning with master arbitration, against scenarios S1 to S10.</summary>
public class FieldVersioningScenarios
{
    [Fact]
    public async Task S1_edits_to_different_fields_are_both_kept()
    {
        var w = await FieldWorld.StartAsync();

        w.Preparer.SetField(w.Paper, FieldWorld.SampleSize, "30");
        w.Second.SetField(w.Paper, FieldWorld.Exceptions, "2");
        await w.SyncAllAsync();

        Assert.Equal("30", w.OnMaster.Value(FieldWorld.SampleSize));
        Assert.Equal("2", w.OnMaster.Value(FieldWorld.Exceptions));
        Assert.Empty(w.OnMaster.Conflicts);
        w.AssertConverged();
    }

    [Fact]
    public async Task S2_same_field_is_flagged_and_both_values_are_kept_until_a_person_decides()
    {
        var w = await FieldWorld.StartAsync();

        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "2");
        var losing = w.Second.SetField(w.Paper, FieldWorld.Exceptions, "3");
        await w.SyncAllAsync();

        // The first to reach the master holds the field; the second is not applied and not lost.
        Assert.Equal("2", w.OnMaster.Value(FieldWorld.Exceptions));
        var conflict = Assert.Single(w.OnMaster.OpenConflicts);
        Assert.Equal((losing, "3", "2", w.Second.UserId), (conflict.Id, conflict.ProposedValue, conflict.MasterValue, conflict.Author));
        Assert.Equal(PendingState.Conflict, Assert.Single(w.Second.NeedsAttention).State);
        // Everyone sees the open conflict, not only its author.
        Assert.Single(w.Manager.Confirmed(w.Paper).OpenConflicts);
        w.AssertConverged();

        // An open conflict blocks sign-off.
        w.Preparer.Submit(w.Paper);
        await w.SyncAllAsync();
        Assert.Equal(ChangeOutcome.Rejected, w.Master.SignOff(w.Engagement, w.Paper, EngagementRole.ManagerQa, w.Manager.UserId).Outcome);

        w.Manager.Resolve(w.Paper, conflict.Id, "3");
        await w.SyncAllAsync();

        Assert.Equal("3", w.OnMaster.Value(FieldWorld.Exceptions));
        Assert.Empty(w.OnMaster.OpenConflicts);
        Assert.Equal(w.Manager.UserId, w.OnMaster.Conflicts[conflict.Id].ResolvedBy);
        Assert.Empty(w.Second.NeedsAttention);
        // The trail shows who set what, from what: 0 -> 2 by the preparer, 2 -> 3 by the manager.
        Assert.Equal(
            [("0", "2", w.Preparer.UserId), ("2", "3", w.Manager.UserId)],
            w.OnMaster.History.Where(h => h.Key == FieldWorld.Exceptions).Skip(1).Select(h => (h.Before!, h.After, h.Author)));
        w.AssertConverged();
    }

    [Fact]
    public async Task S2_several_offline_saves_by_one_author_do_not_conflict_with_themselves_and_all_appear_in_the_trail()
    {
        var w = await FieldWorld.StartAsync();

        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "1");
        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "2");
        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "4");
        await w.SyncAllAsync();

        Assert.Equal("4", w.OnMaster.Value(FieldWorld.Exceptions));
        Assert.Empty(w.OnMaster.Conflicts);
        Assert.Equal(["0", "1", "2", "4"], w.OnMaster.History.Where(h => h.Key == FieldWorld.Exceptions).Select(h => h.After));
    }

    [Fact]
    public async Task S3_concurrent_rewrites_of_the_conclusion_are_never_blended()
    {
        var w = await FieldWorld.StartAsync();
        const string mine = "Two exceptions noted in the sample of 25 items.";
        const string theirs = "No exceptions noted in the sample of 25 items. Control operates effectively.";

        w.Preparer.SetField(w.Paper, FieldWorld.Conclusion, mine);
        w.Second.SetField(w.Paper, FieldWorld.Conclusion, theirs);
        await w.SyncAllAsync();

        // The master holds exactly one author's sentence; the other is preserved word for word in the conflict.
        Assert.Equal(mine, w.OnMaster.Value(FieldWorld.Conclusion));
        Assert.Equal(theirs, Assert.Single(w.OnMaster.OpenConflicts).ProposedValue);
        w.AssertConverged();
    }

    [Fact]
    public async Task S4_review_note_survives_a_concurrent_edit_of_the_field_and_blocks_sign_off()
    {
        var w = await FieldWorld.StartAsync();
        var note = Guid.NewGuid();
        w.Preparer.Submit(w.Paper);
        await w.SyncAllAsync();

        w.Manager.RaiseNote(w.Paper, note, FieldWorld.Conclusion, "Sample of 25 is below the 30 required by the methodology.");
        w.Preparer.SetField(w.Paper, FieldWorld.Conclusion, "No exceptions noted in the sample of 25 items; see cash count sheet.");
        await w.SyncAllAsync();

        Assert.False(w.OnMaster.Notes[note].IsCleared);
        Assert.EndsWith("see cash count sheet.", w.OnMaster.Value(FieldWorld.Conclusion), StringComparison.Ordinal);
        Assert.Empty(w.OnMaster.Conflicts);
        var signOff = w.Master.SignOff(w.Engagement, w.Paper, EngagementRole.ManagerQa, w.Manager.UserId);
        Assert.Equal(ChangeOutcome.Rejected, signOff.Outcome);
        Assert.Contains("review notes", signOff.Reason, StringComparison.Ordinal);
        w.AssertConverged();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S5_a_note_is_never_cleared_over_a_response_the_reviewer_has_not_read(bool reviewerSyncsFirst)
    {
        var w = await FieldWorld.StartAsync();
        var note = Guid.NewGuid();
        w.Manager.RaiseNote(w.Paper, note, FieldWorld.SampleSize, "Why 25?");
        await w.SyncAllAsync();
        w.Preparer.Respond(w.Paper, note, "Population is 180; 25 per the sampling table.");
        await w.SyncAllAsync();

        // Offline at the same time: the reviewer clears on the first response, the preparer adds a correction.
        w.Manager.ClearNote(w.Paper, note);
        w.Preparer.Respond(w.Paper, note, "Correction: population is 310, sample should have been 30.");
        if (reviewerSyncsFirst)
        {
            await w.Manager.SyncAsync(w.Master);
            await w.Preparer.SyncAsync(w.Master);
        }
        else
        {
            await w.Preparer.SyncAsync(w.Master);
            await w.Manager.SyncAsync(w.Master);
        }

        await w.SyncAllAsync();

        if (reviewerSyncsFirst)
        {
            // Cleared on what the reviewer read; the late correction is refused and stays with the preparer.
            Assert.True(w.OnMaster.Notes[note].IsCleared);
            Assert.Single(w.OnMaster.Notes[note].Responses);
            var rejected = Assert.Single(w.Preparer.NeedsAttention);
            Assert.Equal(PendingState.Rejected, rejected.State);
            Assert.Contains("already cleared", rejected.Reason, StringComparison.Ordinal);
        }
        else
        {
            // The correction arrived first, so the stale clearance is refused and the note stays open.
            Assert.False(w.OnMaster.Notes[note].IsCleared);
            Assert.Equal(2, w.OnMaster.Notes[note].Responses.Count);
            Assert.Contains("has not read", Assert.Single(w.Manager.NeedsAttention).Reason, StringComparison.Ordinal);
        }

        w.AssertConverged();
    }

    [Fact]
    public async Task S6_evidence_links_follow_master_order()
    {
        var w = await FieldWorld.StartAsync();
        w.Preparer.LinkEvidence(w.Paper, "hash-count-sheet");
        await w.SyncAllAsync();

        w.Preparer.LinkEvidence(w.Paper, "hash-bank-confirmation");
        w.Second.UnlinkEvidence(w.Paper, "hash-count-sheet");
        w.Second.LinkEvidence(w.Paper, "hash-bank-confirmation");
        await w.SyncAllAsync();

        Assert.Equal(["hash-bank-confirmation"], w.OnMaster.Evidence);
        w.AssertConverged();
    }

    [Fact]
    public async Task S7_edit_after_sign_off_is_accepted_breaks_the_seal_and_leaves_the_sign_off_on_record()
    {
        var w = await FieldWorld.StartAsync();
        w.Preparer.Submit(w.Paper);
        await w.SyncAllAsync();
        Assert.Equal(ChangeOutcome.Accepted, w.Master.SignOff(w.Engagement, w.Paper, EngagementRole.ManagerQa, w.Manager.UserId).Outcome);
        await w.SyncAllAsync();

        // Criterion 3: every replica reproduces the hash the master sealed.
        var signedHash = Assert.Single(w.OnMaster.SignOffs).ContentHash;
        Assert.Equal(signedHash, w.Second.Confirmed(w.Paper).ComputeContentHash());
        Assert.Equal(WorkingPaperStatus.ManagerQaSignedOff, w.Preparer.Confirmed(w.Paper).Status);

        var breaking = w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "1");
        await w.SyncAllAsync();

        Assert.Equal("1", w.OnMaster.Value(FieldWorld.Exceptions));
        var broken = Assert.Single(w.OnMaster.SignOffs);
        Assert.Equal((signedHash, breaking), (broken.ContentHash, broken.BrokenByChangeId));
        Assert.Empty(w.OnMaster.ValidSignOffs);
        Assert.Equal(WorkingPaperStatus.ReadyForReview, w.OnMaster.Status);
        w.AssertConverged();

        // The paper can be signed again; the new seal covers the new content.
        Assert.Equal(ChangeOutcome.Accepted, w.Master.SignOff(w.Engagement, w.Paper, EngagementRole.ManagerQa, w.Manager.UserId).Outcome);
        Assert.NotEqual(signedHash, w.OnMaster.ValidSignOffs.Single().ContentHash);
    }

    [Fact]
    public async Task S7_stale_offline_edit_arriving_after_sign_off_is_flagged_and_does_not_break_the_seal()
    {
        var w = await FieldWorld.StartAsync();

        // The second auditor edits offline on Monday and stays offline.
        w.Second.SetField(w.Paper, FieldWorld.Exceptions, "3");
        // Meanwhile the preparer corrects the same field, submits, and the manager signs off.
        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "2");
        w.Preparer.Submit(w.Paper);
        await w.Preparer.SyncAsync(w.Master);
        Assert.Equal(ChangeOutcome.Accepted, w.Master.SignOff(w.Engagement, w.Paper, EngagementRole.ManagerQa, w.Manager.UserId).Outcome);

        await w.SyncAllAsync();

        // The late edit was written against a value that no longer exists: it is flagged, the signed content is untouched.
        Assert.Equal("2", w.OnMaster.Value(FieldWorld.Exceptions));
        Assert.Single(w.OnMaster.OpenConflicts);
        Assert.Single(w.OnMaster.ValidSignOffs);
        Assert.Equal(WorkingPaperStatus.ManagerQaSignedOff, w.OnMaster.Status);

        // Choosing the late value is a content change after sign-off, so it breaks the seal.
        var resolution = w.Manager.Resolve(w.Paper, w.OnMaster.OpenConflicts.Single().Id, "3");
        await w.SyncAllAsync();
        Assert.Equal(resolution, w.OnMaster.SignOffs.Single().BrokenByChangeId);
        w.AssertConverged();
    }

    [Fact]
    public async Task S7_sign_off_cannot_be_pushed_from_a_client()
    {
        var w = await FieldWorld.StartAsync();
        w.Preparer.Submit(w.Paper);
        await w.SyncAllAsync();

        w.Manager.EnqueueRaw(w.Paper, new SignOffPaper(nameof(EngagementRole.ManagerQa), w.Manager.Confirmed(w.Paper).ComputeContentHash()));
        await w.SyncAllAsync();

        Assert.Empty(w.OnMaster.SignOffs);
        Assert.Contains("stepped-up", Assert.Single(w.Manager.NeedsAttention).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S8_changes_after_engagement_lock_are_rejected_kept_by_the_author_and_logged()
    {
        var w = await FieldWorld.StartAsync();
        var access = new SwitchableAccess();
        var auditLog = new InMemoryAuditLog();
        var coordinator = new SyncCoordinator(access, w.Master, auditLog, TimeProvider.System);
        var line = new ViaCoordinator(coordinator, w.Second.UserId);

        w.Second.SetField(w.Paper, FieldWorld.Exceptions, "5");
        access.Locked = true;
        await w.Second.SyncAsync(line);

        Assert.Equal("0", w.OnMaster.Value(FieldWorld.Exceptions));
        var kept = Assert.Single(w.Second.NeedsAttention);
        Assert.Equal((PendingState.Rejected, "Engagement is locked."), (kept.State, kept.Reason));
        // The laptop's confirmed replica is still exactly what the master archived.
        Assert.Equal(w.OnMaster.Fingerprint(), w.Second.Confirmed(w.Paper).Fingerprint());
    }

    [Theory]
    [InlineData(SyncFault.LostAck)]
    [InlineData(SyncFault.DeliveredTwice)]
    public async Task S9_redelivery_changes_nothing(SyncFault fault)
    {
        var w = await FieldWorld.StartAsync();
        var before = w.Master.LogLength(w.Engagement);

        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "1");
        await w.Preparer.SyncAsync(w.Master, fault);
        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "2");
        await w.SyncAllAsync();

        Assert.Equal(before + 2, w.Master.LogLength(w.Engagement));
        Assert.Equal("2", w.OnMaster.Value(FieldWorld.Exceptions));
        Assert.Empty(w.OnMaster.Conflicts);
        Assert.Empty(w.Preparer.Outbox);
        w.AssertConverged();
    }

    [Fact]
    public async Task S9_redelivered_conflict_is_still_a_conflict()
    {
        var w = await FieldWorld.StartAsync();
        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.SyncAsync(w.Master);

        w.Second.SetField(w.Paper, FieldWorld.Exceptions, "3");
        await w.Second.SyncAsync(w.Master, SyncFault.LostAck);
        await w.Second.SyncAsync(w.Master);

        // The second delivery must not be read as "delivered": the author still has to be told.
        Assert.Equal(PendingState.Conflict, Assert.Single(w.Second.NeedsAttention).State);
        Assert.Single(w.OnMaster.Conflicts);
    }

    [Fact]
    public async Task S10_a_laptop_clock_one_day_ahead_has_no_influence()
    {
        var w = await FieldWorld.StartAsync(secondClock: new SkewedClock(TimeSpan.FromDays(1)));

        w.Second.SetField(w.Paper, FieldWorld.Exceptions, "9");
        w.Preparer.SetField(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.SyncAsync(w.Master);
        await w.Second.SyncAsync(w.Master);

        // Arrival order at the master decides which value holds; the "later" timestamp wins nothing and is flagged.
        Assert.Equal("2", w.OnMaster.Value(FieldWorld.Exceptions));
        Assert.Equal("9", Assert.Single(w.OnMaster.OpenConflicts).ProposedValue);
    }

    [Fact]
    public void S10_control_last_writer_wins_by_wall_clock_lets_the_skewed_laptop_win_silently()
    {
        var hanoi = new LwwFieldMap(Guid.NewGuid(), new SkewedClock(TimeSpan.Zero));
        var skewed = new LwwFieldMap(Guid.NewGuid(), new SkewedClock(TimeSpan.FromDays(1)));

        skewed.Set(FieldWorld.Exceptions, "9");
        // Written a real hour later, by a person who is right, on a laptop whose clock is right.
        var later = new LwwFieldMap(Guid.NewGuid(), new SkewedClock(TimeSpan.FromHours(1)));
        later.Set(FieldWorld.Exceptions, "2");
        hanoi.MergeFrom(later);
        hanoi.MergeFrom(skewed);

        Assert.Equal("9", hanoi.Get(FieldWorld.Exceptions));
    }
}
