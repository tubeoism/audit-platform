using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;
using AuditPlatform.Master.Data.Tests;
using AuditPlatform.Master.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Sync.Tests;

/// <summary>
/// ADR-0004 scenarios S1 to S10, each against the reference model and against the durable PostgreSQL engine, with
/// real SQLite replicas and the real sync agent on the laptops.
/// </summary>
public sealed class Scenarios(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S1_edits_to_different_fields_are_both_kept(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);

        await w.Preparer.SetAsync(w.Paper, FieldWorld.SampleSize, "30");
        await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        Assert.Equal("30", master.Value(FieldWorld.SampleSize));
        Assert.Equal("2", master.Value(FieldWorld.Exceptions));
        Assert.Empty(master.Conflicts);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S2_same_field_is_flagged_and_both_values_are_kept_until_a_person_decides(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);

        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        var losing = await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "3");
        await w.SyncAllAsync();

        // The first to reach the master holds the field; the second is not applied and not lost.
        var master = await w.OnMasterAsync();
        Assert.Equal("2", master.Value(FieldWorld.Exceptions));
        var conflict = Assert.Single(master.OpenConflicts);
        Assert.Equal((losing, "3", "2", w.Second.UserId), (conflict.Id, conflict.ProposedValue, conflict.MasterValue, conflict.Author));
        var flagged = Assert.Single(await w.Second.NeedsAttentionAsync());
        Assert.Equal((OutboxState.Conflict, losing), (flagged.State, flagged.ChangeId));
        // The author still sees what the master holds, not the value that was not applied.
        Assert.Equal("2", await w.Second.Work.ViewAsync(w.Paper, FieldWorld.Exceptions, Ct));
        // Everyone sees the open conflict, not only its author.
        Assert.Single((await w.Manager.ConfirmedAsync(w.Paper)).OpenConflicts);
        await w.AssertConvergedAsync();

        // An open conflict blocks sign-off.
        await w.Preparer.Work.SubmitForReviewAsync(w.Paper, Ct);
        await w.SyncAllAsync();
        var refused = await w.SignOffAsync();
        Assert.Equal(SignOffStatus.Refused, refused.Status);
        Assert.Contains("conflicts", refused.Reason, StringComparison.Ordinal);

        // Deciding is for the person accountable for the paper (its preparer) or the engagement manager: not for the
        // reviewer, who must stay independent of the content, and not for the other auditor.
        foreach (var notEntitled in new[] { w.Manager, w.Second })
        {
            var attempt = await notEntitled.Work.ResolveConflictAsync(w.Paper, conflict.Id, "3", Ct);
            await w.SyncAllAsync();
            var refusedResolve = Assert.Single(await notEntitled.OutboxAsync(), o => o.ChangeId == attempt);
            Assert.Equal((OutboxState.Rejected, FieldPolicy.NotTheOwner), (refusedResolve.State, refusedResolve.LastError));
            await notEntitled.Work.DismissRejectedAsync(attempt, Ct);
        }

        Assert.Single((await w.OnMasterAsync()).OpenConflicts);

        await w.Preparer.Work.ResolveConflictAsync(w.Paper, conflict.Id, "3", Ct);
        await w.SyncAllAsync();

        master = await w.OnMasterAsync();
        Assert.Equal("3", master.Value(FieldWorld.Exceptions));
        Assert.Empty(master.OpenConflicts);
        Assert.Equal(w.Preparer.UserId, master.Conflicts[conflict.Id].ResolvedBy);
        Assert.Empty(await w.Second.NeedsAttentionAsync());
        // The trail shows who set what, from what: 0 -> 2 by the preparer, 2 -> 3 by the preparer deciding the conflict.
        Assert.Equal(
            [("0", "2", w.Preparer.UserId), ("2", "3", w.Preparer.UserId)],
            master.History.Where(h => h.Key == FieldWorld.Exceptions).Skip(1).Select(h => (h.Before!, h.After, h.Author)));
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S2_several_offline_saves_by_one_author_do_not_conflict_with_themselves_and_all_appear_in_the_trail(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);

        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "1");
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "4");
        Assert.Equal("4", await w.Preparer.Work.ViewAsync(w.Paper, FieldWorld.Exceptions, Ct));
        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        Assert.Equal("4", master.Value(FieldWorld.Exceptions));
        Assert.Empty(master.Conflicts);
        Assert.Equal(["0", "1", "2", "4"], master.History.Where(h => h.Key == FieldWorld.Exceptions).Select(h => h.After));
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S2_a_later_offline_save_replaces_the_authors_own_flagged_proposal(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);

        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.SyncAsync();
        // Two offline saves on stale data: the first conflicts, the second is chained on it.
        var first = await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "3");
        var second = await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "5");
        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        Assert.Equal("2", master.Value(FieldWorld.Exceptions));
        Assert.Equal(ConflictStatus.Superseded, master.Conflicts[first].Status);
        Assert.Equal((second, "5"), (Assert.Single(master.OpenConflicts).Id, master.Conflicts[second].ProposedValue));
        Assert.Equal(second, Assert.Single(await w.Second.NeedsAttentionAsync()).ChangeId);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S3_concurrent_rewrites_of_the_conclusion_are_never_blended(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        const string mine = "Two exceptions noted in the sample of 25 items.";
        const string theirs = "No exceptions noted in the sample of 25 items. Control operates effectively.";

        await w.Preparer.SetAsync(w.Paper, FieldWorld.Conclusion, mine);
        await w.Second.SetAsync(w.Paper, FieldWorld.Conclusion, theirs);
        await w.SyncAllAsync();

        // The master holds exactly one author's sentence; the other is preserved word for word in the conflict.
        var master = await w.OnMasterAsync();
        Assert.Equal(mine, master.Value(FieldWorld.Conclusion));
        Assert.Equal(theirs, Assert.Single(master.OpenConflicts).ProposedValue);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S4_review_note_survives_a_concurrent_edit_of_the_field_and_blocks_sign_off(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        var note = Guid.NewGuid();
        await w.Preparer.Work.SubmitForReviewAsync(w.Paper, Ct);
        await w.SyncAllAsync();

        await w.Manager.Work.RaiseNoteAsync(w.Paper, note, FieldWorld.Conclusion, "Sample of 25 is below the 30 required by the methodology.", Ct);
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Conclusion, "No exceptions noted in the sample of 25 items; see cash count sheet.");
        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        Assert.False(master.Notes[note].IsCleared);
        Assert.EndsWith("see cash count sheet.", master.Value(FieldWorld.Conclusion), StringComparison.Ordinal);
        Assert.Empty(master.Conflicts);
        var signOff = await w.SignOffAsync();
        Assert.Equal(SignOffStatus.Refused, signOff.Status);
        Assert.Contains("review notes", signOff.Reason, StringComparison.Ordinal);
        await w.AssertConvergedAsync();
    }

    [Theory]
    [InlineData(MasterKind.Memory, true), InlineData(MasterKind.Memory, false)]
    [InlineData(MasterKind.Postgres, true), InlineData(MasterKind.Postgres, false)]
    public async Task S5_a_note_is_never_cleared_over_a_response_the_reviewer_has_not_read(MasterKind kind, bool reviewerSyncsFirst)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        var note = Guid.NewGuid();
        await w.Manager.Work.RaiseNoteAsync(w.Paper, note, FieldWorld.SampleSize, "Why 25?", Ct);
        await w.SyncAllAsync();
        await w.Preparer.Work.RespondAsync(w.Paper, note, "Population is 180; 25 per the sampling table.", Ct);
        await w.SyncAllAsync();

        // Offline at the same time: the reviewer clears on the first response, the preparer adds a correction.
        await w.Manager.Work.ClearNoteAsync(w.Paper, note, Ct);
        await w.Preparer.Work.RespondAsync(w.Paper, note, "Correction: population is 310, sample should have been 30.", Ct);
        if (reviewerSyncsFirst)
        {
            await w.Manager.SyncAsync();
            await w.Preparer.SyncAsync();
        }
        else
        {
            await w.Preparer.SyncAsync();
            await w.Manager.SyncAsync();
        }

        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        if (reviewerSyncsFirst)
        {
            // Cleared on what the reviewer read; the late correction is refused and stays with the preparer.
            Assert.True(master.Notes[note].IsCleared);
            Assert.Single(master.Notes[note].Responses);
            var rejected = Assert.Single(await w.Preparer.NeedsAttentionAsync());
            Assert.Equal(OutboxState.Rejected, rejected.State);
            Assert.Contains("already cleared", rejected.LastError, StringComparison.Ordinal);
        }
        else
        {
            // The correction arrived first, so the stale clearance is refused and the note stays open.
            Assert.False(master.Notes[note].IsCleared);
            Assert.Equal(2, master.Notes[note].Responses.Count);
            Assert.Contains("has not read", Assert.Single(await w.Manager.NeedsAttentionAsync()).LastError, StringComparison.Ordinal);
        }

        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S6_evidence_links_follow_master_order(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        var (countSheet, bankConfirmation) = (new string('a', 64), new string('b', 64));
        await w.Master.ReceiveEvidenceAsync(w.Engagement, countSheet, w.Preparer.UserId);
        await w.Master.ReceiveEvidenceAsync(w.Engagement, bankConfirmation, w.Preparer.UserId);
        await w.Preparer.Work.LinkEvidenceAsync(w.Paper, countSheet, Ct);
        await w.SyncAllAsync();

        await w.Preparer.Work.LinkEvidenceAsync(w.Paper, bankConfirmation, Ct);
        await w.Second.Work.UnlinkEvidenceAsync(w.Paper, countSheet, Ct);
        await w.Second.Work.LinkEvidenceAsync(w.Paper, bankConfirmation, Ct);
        await w.SyncAllAsync();

        Assert.Equal([bankConfirmation], (await w.OnMasterAsync()).Evidence);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S6_a_link_to_a_file_the_master_does_not_hold_is_refused(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        var (missing, elsewhere) = (new string('c', 64), new string('d', 64));

        // A file received for another engagement does not count: evidence belongs to the engagement it was received for.
        await w.Master.ReceiveEvidenceAsync(Guid.NewGuid(), elsewhere, w.Preparer.UserId);
        await w.Preparer.Work.LinkEvidenceAsync(w.Paper, missing, Ct);
        await w.Preparer.Work.LinkEvidenceAsync(w.Paper, elsewhere, Ct);
        await w.Preparer.Work.LinkEvidenceAsync(w.Paper, "not-a-hash", Ct);
        await w.SyncAllAsync();

        Assert.Empty((await w.OnMasterAsync()).Evidence);
        Assert.All(await w.Preparer.OutboxAsync(), o => Assert.Equal((OutboxState.Rejected, FieldPolicy.EvidenceNotReceived), (o.State, o.LastError)));
        Assert.Equal(3, (await w.Preparer.OutboxAsync()).Count);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S7_edit_after_sign_off_is_accepted_breaks_the_seal_and_leaves_the_sign_off_on_record(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        await w.Preparer.Work.SubmitForReviewAsync(w.Paper, Ct);
        await w.SyncAllAsync();
        Assert.Equal(SignOffStatus.SignedOff, (await w.SignOffAsync()).Status);
        await w.SyncAllAsync();

        // Criterion 3: every replica reproduces the hash the master sealed.
        var signedHash = Assert.Single((await w.OnMasterAsync()).SignOffs).ContentHash;
        Assert.Equal(signedHash, (await w.Second.ConfirmedAsync(w.Paper)).ComputeContentHash());
        Assert.Equal(WorkingPaperStatus.ManagerQaSignedOff, (await w.Preparer.ConfirmedAsync(w.Paper)).Status);

        var breaking = await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "1");
        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        Assert.Equal("1", master.Value(FieldWorld.Exceptions));
        var broken = Assert.Single(master.SignOffs);
        Assert.Equal((signedHash, breaking, w.Manager.UserId), (broken.ContentHash, broken.BrokenByChangeId, broken.SignedBy));
        Assert.Empty(master.ValidSignOffs);
        Assert.Equal(WorkingPaperStatus.ReadyForReview, master.Status);
        await w.AssertConvergedAsync();

        // The reviewer's laptop is told when it replays the breaking change: which paper, which change, by whom.
        var told = Assert.Single(w.Manager.SealBreaks);
        Assert.Equal((w.Paper, breaking, w.Preparer.UserId), (told.DocumentId, told.ChangeId, told.ChangedBy));
        Assert.Equal(signedHash, Assert.Single(told.BrokenSignOffs).ContentHash);

        // The paper can be signed again; the new seal covers the new content.
        Assert.Equal(SignOffStatus.SignedOff, (await w.SignOffAsync()).Status);
        Assert.NotEqual(signedHash, (await w.OnMasterAsync()).ValidSignOffs.Single().ContentHash);
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task Roles_keep_preparation_and_review_apart(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);

        // A reviewer does not write the content they review, on this paper or a new one.
        var edit = await w.Manager.SetAsync(w.Paper, FieldWorld.Conclusion, "Rewritten by the reviewer.");
        var create = await w.Manager.Work.CreatePaperAsync(Guid.NewGuid(), "AUP-CSH-04", 1, Ct);
        // A preparer does not raise review notes.
        var note = await w.Second.Work.RaiseNoteAsync(w.Paper, Guid.NewGuid(), FieldWorld.Conclusion, "Looks fine to me.", Ct);
        // A preparer does not create a paper in someone else's name.
        var assigned = await w.Second.Work.EnqueueAsync(Guid.NewGuid(), new CreatePaper("AUP-CSH-05", 1, w.Preparer.UserId), Ct);
        await w.SyncAllAsync();

        var managerOutbox = await w.Manager.OutboxAsync();
        Assert.All(managerOutbox, o => Assert.Equal((OutboxState.Rejected, FieldPolicy.NotAnEditor), (o.State, o.LastError)));
        Assert.Equal([edit, create], managerOutbox.Select(o => o.ChangeId));
        Assert.Equal(
            [(note, FieldPolicy.NotAReviewer), (assigned, FieldPolicy.NotYourPaper)],
            (await w.Second.OutboxAsync()).Select(o => (o.ChangeId, o.LastError!)));

        // Nothing of it reached the record, and what each role is for still works.
        var master = await w.OnMasterAsync();
        Assert.Equal("No exceptions noted in the sample of 25 items.", master.Value(FieldWorld.Conclusion));
        Assert.Empty(master.Notes);
        var raised = Guid.NewGuid();
        await w.Manager.Work.RaiseNoteAsync(w.Paper, raised, FieldWorld.Conclusion, "State the population.", Ct);
        await w.SyncAllAsync();
        await w.Second.Work.RespondAsync(w.Paper, raised, "Population: 412 vouchers.", Ct);
        await w.SyncAllAsync();
        Assert.Single((await w.OnMasterAsync()).Notes[raised].Responses);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S7_stale_offline_edit_arriving_after_sign_off_is_flagged_and_does_not_break_the_seal(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);

        // The second auditor edits offline on Monday and stays offline.
        await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "3");
        // Meanwhile the preparer corrects the same field, submits, and the manager signs off.
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.Work.SubmitForReviewAsync(w.Paper, Ct);
        await w.Preparer.SyncAsync();
        Assert.Equal(SignOffStatus.SignedOff, (await w.SignOffAsync()).Status);

        await w.SyncAllAsync();

        // The late edit was written against a value that no longer exists: it is flagged, the signed content is untouched.
        var master = await w.OnMasterAsync();
        Assert.Equal("2", master.Value(FieldWorld.Exceptions));
        Assert.Single(master.OpenConflicts);
        Assert.Single(master.ValidSignOffs);
        Assert.Equal(WorkingPaperStatus.ManagerQaSignedOff, master.Status);
        Assert.Empty(w.Manager.SealBreaks);

        // Choosing the late value is a content change after sign-off, so it breaks the seal.
        var resolution = await w.Preparer.Work.ResolveConflictAsync(w.Paper, master.OpenConflicts.Single().Id, "3", Ct);
        await w.SyncAllAsync();
        Assert.Equal(resolution, (await w.OnMasterAsync()).SignOffs.Single().BrokenByChangeId);
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S7_sign_off_cannot_be_pushed_from_a_client(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        await w.Preparer.Work.SubmitForReviewAsync(w.Paper, Ct);
        await w.SyncAllAsync();

        var hash = (await w.Manager.ConfirmedAsync(w.Paper)).ComputeContentHash();
        await w.Manager.Work.EnqueueAsync(w.Paper, new SignOffPaper(nameof(EngagementRole.ManagerQa), hash), Ct);
        await w.SyncAllAsync();

        Assert.Empty((await w.OnMasterAsync()).SignOffs);
        Assert.Contains("stepped-up", Assert.Single(await w.Manager.NeedsAttentionAsync()).LastError, StringComparison.Ordinal);
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S8_changes_after_engagement_lock_are_rejected_kept_by_the_author_and_the_replica_equals_the_archive(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        var head = await w.Master.HeadAsync(w.Engagement);

        var late = await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "5");
        await w.Master.LockAsync(w.Engagement);
        await w.Second.SyncAsync();

        Assert.Equal("0", (await w.OnMasterAsync()).Value(FieldWorld.Exceptions));
        Assert.Equal(head, await w.Master.HeadAsync(w.Engagement));
        var kept = Assert.Single(await w.Second.NeedsAttentionAsync());
        Assert.Equal((late, OutboxState.Rejected, FieldSyncEngine.LockedReason), (kept.ChangeId, kept.State, kept.LastError));
        // The author's text is still on the laptop, word for word.
        Assert.Equal("5", ((SetField)FieldPayload.Decode(kept.Payload)).Value);
        // The laptop's confirmed replica is still exactly what the master archived.
        await w.AssertConvergedAsync();
        // Sign-off is refused as well.
        Assert.NotEqual(SignOffStatus.SignedOff, (await w.SignOffAsync()).Status);

        // Once the laptop knows about the lock, it refuses new edits at the door.
        await w.Second.Agent.MarkLockedAsync(w.Engagement, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "6"));
    }

    [Theory]
    [InlineData(MasterKind.Memory, SyncFault.LostAck), InlineData(MasterKind.Memory, SyncFault.DeliveredTwice), InlineData(MasterKind.Memory, SyncFault.Dropped)]
    [InlineData(MasterKind.Postgres, SyncFault.LostAck), InlineData(MasterKind.Postgres, SyncFault.DeliveredTwice), InlineData(MasterKind.Postgres, SyncFault.Dropped)]
    public async Task S9_redelivery_changes_nothing(MasterKind kind, SyncFault fault)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        var before = await w.Master.HeadAsync(w.Engagement);

        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "1");
        await w.Preparer.SyncAsync(fault);
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.SyncAllAsync();

        var master = await w.OnMasterAsync();
        Assert.Equal(before + 2, await w.Master.HeadAsync(w.Engagement));
        Assert.Equal("2", master.Value(FieldWorld.Exceptions));
        Assert.Empty(master.Conflicts);
        Assert.Empty(await w.Preparer.OutboxAsync());
        await w.AssertConvergedAsync();
    }

    [Theory]
    [InlineData(MasterKind.Memory, SyncFault.LostAck), InlineData(MasterKind.Memory, SyncFault.Dropped)]
    [InlineData(MasterKind.Postgres, SyncFault.LostAck), InlineData(MasterKind.Postgres, SyncFault.Dropped)]
    public async Task S9_redelivered_conflict_is_still_a_conflict(MasterKind kind, SyncFault fault)
    {
        using var w = await FieldWorld.StartAsync(kind, pg);
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.SyncAsync();

        await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "3");
        await w.Second.SyncAsync(fault);
        await w.Second.SyncAsync();

        // Whether the laptop learns it from the redelivery or from the log, it must not read "delivered":
        // the author still has to be told.
        Assert.Equal(OutboxState.Conflict, Assert.Single(await w.Second.NeedsAttentionAsync()).State);
        Assert.Single((await w.OnMasterAsync()).Conflicts);
        await w.SyncAllAsync();
        await w.AssertConvergedAsync();
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S10_a_laptop_clock_one_day_ahead_has_no_influence(MasterKind kind)
    {
        using var w = await FieldWorld.StartAsync(kind, pg, secondClock: new SkewedClock(TimeSpan.FromDays(1)));

        await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "9");
        await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        await w.Preparer.SyncAsync();
        await w.Second.SyncAsync();

        // Arrival order at the master decides which value holds; the "later" timestamp wins nothing and is flagged.
        var master = await w.OnMasterAsync();
        Assert.Equal("2", master.Value(FieldWorld.Exceptions));
        Assert.Equal("9", Assert.Single(master.OpenConflicts).ProposedValue);
    }

    [Theory, InlineData(MasterKind.Memory), InlineData(MasterKind.Postgres)]
    public async Task S10_a_laptop_whose_clock_was_set_back_still_sends_its_saves_in_the_order_they_were_made(MasterKind kind)
    {
        // The clock jumps back a day between two saves of the same field (time zone change, battery, a wrong manual setting).
        var clock = new SteppingClock(TimeSpan.Zero, TimeSpan.FromDays(-1), TimeSpan.FromDays(-2));
        var master = FieldWorld.CreateMaster(kind, pg);
        var engagement = Guid.NewGuid();
        var paper = Guid.NewGuid();
        using var laptop = new Laptop(master, engagement, clock: clock);
        await master.SeedAsync(engagement, [(laptop.UserId, EngagementRole.Preparer)]);

        await laptop.Work.CreatePaperAsync(paper, "AUP-CSH-03", 1, Ct);
        await laptop.SetAsync(paper, FieldWorld.Exceptions, "1");
        await laptop.SetAsync(paper, FieldWorld.Exceptions, "2");
        await laptop.SyncAsync();

        var onMaster = await master.DocumentAsync(engagement, paper);
        Assert.Equal("2", onMaster.Value(FieldWorld.Exceptions));
        Assert.Empty(onMaster.Conflicts);
        Assert.Empty(await laptop.OutboxAsync());
    }

    [Fact]
    public async Task Postgres_master_writes_every_decision_to_the_audit_log_with_before_and_after()
    {
        using var w = await FieldWorld.StartAsync(MasterKind.Postgres, pg);
        var accepted = await w.Preparer.SetAsync(w.Paper, FieldWorld.Exceptions, "2");
        var flagged = await w.Second.SetAsync(w.Paper, FieldWorld.Exceptions, "3");
        await w.SyncAllAsync();
        await w.Manager.Work.EnqueueAsync(w.Paper, new SignOffPaper(nameof(EngagementRole.ManagerQa), "forged"), Ct);
        await w.Manager.SyncAsync();

        await using var db = ((PostgresMaster)w.Master).CreateContext();
        var trail = await db.AuditLog.AsNoTracking().Where(a => a.EngagementId == w.Engagement).OrderBy(a => a.Sequence).ToListAsync(Ct);

        var set = Assert.Single(trail, a => a.Action == "sync.set.accepted" && a.PayloadJson.Contains(accepted.ToString(), StringComparison.Ordinal));
        Assert.Equal((w.Preparer.UserId, "working_paper", w.Paper.ToString("D")), (set.ActorUserId, set.EntityType, set.EntityId));
        Assert.Contains("\"before\":\"0\"", set.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"2\"", set.PayloadJson, StringComparison.Ordinal);

        var conflict = Assert.Single(trail, a => a.Action == "sync.set.conflict");
        Assert.Equal(w.Second.UserId, conflict.ActorUserId);
        Assert.Contains(flagged.ToString(), conflict.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"3\"", conflict.PayloadJson, StringComparison.Ordinal);

        // An attempt to push a sign-off from a laptop is on record too, although it changed nothing.
        Assert.Equal(w.Manager.UserId, Assert.Single(trail, a => a.Action == "sync.sign-off.rejected").ActorUserId);

        // The whole chain, including these entries, is intact.
        var all = await db.AuditLog.AsNoTracking().OrderBy(a => a.Sequence).ToListAsync(Ct);
        Assert.Null(AuditLog.HashChain.Verify(all.Select(r => new AuditLog.AuditEntry(r.Sequence, r.OccurredAt, r.ActorUserId, r.Action,
            r.EntityType, r.EntityId, r.EngagementId, r.PayloadJson, r.PreviousHash, r.Hash))));
    }

    [Fact]
    public async Task Postgres_master_survives_a_restart_and_serialises_concurrent_pushes()
    {
        // Every call in these tests already uses a new DbContext and a new engine, i.e. nothing lives in memory
        // between calls. Here: eight laptops push to the same working paper at the same moment.
        var master = new PostgresMaster(pg);
        var engagement = Guid.NewGuid();
        var paper = Guid.NewGuid();
        var laptops = Enumerable.Range(0, 8).Select(_ => new Laptop(master, engagement)).ToList();
        await master.SeedAsync(engagement, [.. laptops.Select(l => (l.UserId, EngagementRole.Preparer))]);
        await laptops[0].Work.CreatePaperAsync(paper, "AUP-CSH-03", 1, Ct);
        await laptops[0].SyncAsync();

        for (var i = 0; i < laptops.Count; i++)
        {
            for (var n = 0; n < 5; n++)
            {
                await laptops[i].SetAsync(paper, $"field_{i}", $"value-{n}");
                await laptops[i].SetAsync(paper, "shared", $"laptop-{i}-{n}");
            }
        }

        await Task.WhenAll(laptops.Select(l => Task.Run(() => l.SyncAsync(), Ct)));
        foreach (var laptop in laptops)
        {
            await laptop.SyncAsync();
        }

        // Sequences are 1..n without a gap, and every replica folded the same log into the same document.
        await using var db = master.CreateContext();
        var sequences = await db.Changes.AsNoTracking().Where(c => c.EngagementId == engagement).OrderBy(c => c.Sequence).Select(c => c.Sequence).ToListAsync(Ct);
        Assert.Equal(Enumerable.Range(1, 1 + (8 * 10)).Select(i => (long)i), sequences);
        var onMaster = await master.DocumentAsync(engagement, paper);
        Assert.Equal(onMaster.Fingerprint(), (await master.ReplayAsync(engagement, paper)).Fingerprint());
        Assert.All(laptops, l => Assert.Equal(onMaster.Fingerprint(), l.ConfirmedAsync(paper).GetAwaiter().GetResult().Fingerprint()));
        // One laptop's chain holds "shared"; the other seven have one open proposal each. Nothing vanished.
        Assert.Equal(7, onMaster.OpenConflicts.Count());
        Assert.Equal(8 * 10, onMaster.History.Count + onMaster.Conflicts.Count);
        laptops.ForEach(l => l.Dispose());
    }

    [Theory]
    [InlineData("UPDATE changes SET author_user_id = gen_random_uuid()")]
    [InlineData("DELETE FROM changes")]
    [InlineData("TRUNCATE changes")]
    public async Task Database_rejects_mutation_of_the_change_log(string sql)
    {
        using var w = await FieldWorld.StartAsync(MasterKind.Postgres, pg);
        await using var db = ((PostgresMaster)w.Master).CreateContext();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Ct));
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replica_ahead_of_the_master_is_told_to_rebuild_instead_of_drifting()
    {
        // The master was restored from a backup that is older than what this laptop has already pulled.
        using var w = await FieldWorld.StartAsync(MasterKind.Postgres, pg);
        var head = await w.Master.HeadAsync(w.Engagement);

        var ex = await Assert.ThrowsAsync<ReplicaAheadOfMasterException>(() =>
            w.Master.PullAsync(w.Preparer.UserId, w.Engagement, new SyncCursor(head + 3), 100));
        Assert.Contains("rebuilt", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith(SyncErrors.ReplicaMustRebuild, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replica_whose_last_change_is_not_the_masters_is_told_to_rebuild_even_when_the_numbers_line_up()
    {
        // The master was restored from an older backup and has grown past this laptop with different changes:
        // position 3 exists on both sides, but it is not the same change.
        using var w = await FieldWorld.StartAsync(MasterKind.Postgres, pg);
        var user = w.Preparer.UserId;
        var all = (await w.Master.PullAsync(user, w.Engagement, SyncCursor.Start, 100)).Changes;
        var held = all[2];
        var after = all.Count - held.Sequence;

        // The same change at that position, or no id at all (an older client): nothing is wrong.
        Assert.Equal(after, (await w.Master.PullAsync(user, w.Engagement, new SyncCursor(held.Sequence, held.ChangeId), 100)).Changes.Count);
        Assert.Equal(after, (await w.Master.PullAsync(user, w.Engagement, new SyncCursor(held.Sequence), 100)).Changes.Count);

        var ex = await Assert.ThrowsAsync<ReplicaDivergedFromMasterException>(
            () => w.Master.PullAsync(user, w.Engagement, new SyncCursor(held.Sequence, Guid.NewGuid()), 100));
        Assert.StartsWith(SyncErrors.ReplicaMustRebuild, ex.Message, StringComparison.Ordinal);
        Assert.True(SyncErrors.IsRebuildSignal(ex));
    }

    /// <summary>Returns a different, scripted time on every reading; stays on the last one.</summary>
    private sealed class SteppingClock(params TimeSpan[] offsets) : TimeProvider
    {
        private static readonly DateTimeOffset Base = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        private int _reading;

        public override DateTimeOffset GetUtcNow() => Base + offsets[Math.Min(_reading++, offsets.Length - 1)];
    }
}
