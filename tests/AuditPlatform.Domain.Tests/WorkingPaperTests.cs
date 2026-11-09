using AuditPlatform.Domain;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;

namespace AuditPlatform.Domain.Tests;

public class WorkingPaperTests
{
    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly Guid JapanDesk = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static WorkingPaper NewPaper()
    {
        var wp = new WorkingPaper(Guid.NewGuid(), Guid.NewGuid(), "AUP-CSH-03", 1, Preparer);
        wp.SetField("result", "Counted VND 12,500,000; agrees to cash book.");
        wp.SetField("conclusion", "No exceptions.");
        return wp;
    }

    [Fact]
    public void Content_hash_is_stable_and_order_independent()
    {
        var id = Guid.NewGuid();
        var eng = Guid.NewGuid();
        var a = new WorkingPaper(id, eng, "AUP-CSH-03", 1, Preparer);
        a.SetField("b", "2");
        a.SetField("a", "1");
        var b = new WorkingPaper(id, eng, "AUP-CSH-03", 1, Preparer);
        b.SetField("a", "1");
        b.SetField("b", "2");

        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
        Assert.Equal(64, a.ComputeContentHash().Length);
    }

    [Fact]
    public void Two_level_sign_off_in_order()
    {
        var wp = NewPaper();
        wp.SubmitForReview();
        wp.SignOff(EngagementRole.ManagerQa, Manager, Now);
        wp.SignOff(EngagementRole.JapanDesk, JapanDesk, Now.AddHours(1));

        Assert.Equal(WorkingPaperStatus.JapanDeskSignedOff, wp.Status);
        Assert.Equal(2, wp.SignOffs.Count);
        Assert.False(wp.IsSealBroken());
    }

    [Fact]
    public void Japan_desk_cannot_sign_before_manager_qa()
    {
        var wp = NewPaper();
        wp.SubmitForReview();
        Assert.Throws<DomainException>(() => wp.SignOff(EngagementRole.JapanDesk, JapanDesk, Now));
    }

    [Fact]
    public void Preparer_cannot_review_own_paper()
    {
        var wp = NewPaper();
        wp.SubmitForReview();
        Assert.Throws<DomainException>(() => wp.SignOff(EngagementRole.ManagerQa, Preparer, Now));
    }

    [Fact]
    public void Open_review_note_blocks_sign_off_until_cleared()
    {
        var wp = NewPaper();
        wp.SubmitForReview();
        var note = wp.RaiseReviewNote(Manager, "result", "Attach the signed count sheet.", Now);

        Assert.Throws<DomainException>(() => wp.SignOff(EngagementRole.ManagerQa, Manager, Now));

        note.Respond(Preparer, "Attached.");
        note.Clear(Manager);
        wp.SignOff(EngagementRole.ManagerQa, Manager, Now);
        Assert.Equal(WorkingPaperStatus.ManagerQaSignedOff, wp.Status);
    }

    [Fact]
    public void Only_raising_reviewer_can_clear_a_note()
    {
        var wp = NewPaper();
        var note = wp.RaiseReviewNote(Manager, "result", "Clarify.", Now);
        note.Respond(Preparer, "Done.");
        Assert.Throws<DomainException>(() => note.Clear(JapanDesk));
    }

    [Theory]
    [InlineData("field")]
    [InlineData("evidence")]
    public void Any_edit_after_sign_off_breaks_the_seal(string kind)
    {
        var wp = NewPaper();
        wp.SubmitForReview();
        wp.SignOff(EngagementRole.ManagerQa, Manager, Now);

        if (kind == "field")
        {
            wp.SetField("conclusion", "No exceptions noted.");
        }
        else
        {
            wp.LinkEvidence(new string('a', 64));
        }

        Assert.True(wp.IsSealBroken());
    }

    [Fact]
    public void Review_notes_do_not_affect_the_seal()
    {
        var wp = NewPaper();
        wp.SubmitForReview();
        wp.SignOff(EngagementRole.ManagerQa, Manager, Now);
        wp.RaiseReviewNote(JapanDesk, "conclusion", "Translate for client.", Now);
        Assert.False(wp.IsSealBroken());
    }
}
