using System.Text.Json;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;

namespace AuditPlatform.Sync.Tests;

/// <summary>The reducer on its own: rules that the scenarios do not reach, and the stored form of a document.</summary>
public class FieldDocumentTests
{
    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly Guid Director = Guid.NewGuid();

    private static FieldDocument NewPaper()
    {
        var doc = new FieldDocument(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(Decision.Accepted, doc.Apply(Guid.NewGuid(), Preparer, new CreatePaper("AUP-CSH-03", 1, Preparer)).Decision);
        return doc;
    }

    private static Guid Set(FieldDocument doc, Guid author, string key, string value)
    {
        var id = Guid.NewGuid();
        var result = doc.Apply(id, author, new SetField(key, value, doc.Fields.TryGetValue(key, out var cell) ? cell.ChangeId : null));
        Assert.Equal(Decision.Accepted, result.Decision);
        return id;
    }

    private static ApplyResult SignOff(FieldDocument doc, EngagementRole level, Guid reviewer) =>
        doc.Apply(Guid.NewGuid(), reviewer, new SignOffPaper(level.ToString(), doc.ComputeContentHash()));

    [Fact]
    public void A_stored_document_is_restored_exactly_and_keeps_deciding_the_same_way()
    {
        var doc = NewPaper();
        Set(doc, Preparer, "sample_size", "25");
        var first = Set(doc, Preparer, "conclusion", "Không có ngoại lệ — 25 mẫu \"đạt\".\nDòng 2");
        doc.Apply(Guid.NewGuid(), Manager, new SetField("conclusion", "stale proposal", null));
        doc.Apply(Guid.NewGuid(), Preparer, new LinkEvidence("hash-a"));
        var note = Guid.NewGuid();
        doc.Apply(Guid.NewGuid(), Manager, new RaiseNote(note, "sample_size", "Why 25?"));
        var response = Guid.NewGuid();
        doc.Apply(response, Preparer, new RespondNote(note, "Sampling table."));
        doc.Apply(Guid.NewGuid(), Manager, new ClearNote(note, response));
        doc.Apply(Guid.NewGuid(), Preparer, new SubmitForReview());

        var restored = FieldDocument.FromJson(doc.ToJson());

        Assert.Equal(doc.Fingerprint(), restored.Fingerprint());
        Assert.Equal(doc.ComputeContentHash(), restored.ComputeContentHash());
        Assert.Equal(first, restored.Fields["conclusion"].ChangeId);
        Assert.Equal(doc.History, restored.History);
        Assert.Single(restored.OpenConflicts);

        // Both copies refuse the same sign-off for the same reason, and accept the same resolution.
        Assert.Equal(doc.WhySignOffIsRefused(nameof(EngagementRole.ManagerQa), Manager), restored.WhySignOffIsRefused(nameof(EngagementRole.ManagerQa), Manager));
        var resolve = new ResolveConflict(restored.OpenConflicts.Single().Id, "stale proposal", first);
        var id = Guid.NewGuid();
        Assert.Equal(doc.Apply(id, Manager, resolve).Decision, restored.Apply(id, Manager, resolve).Decision);
        Assert.Equal(doc.Fingerprint(), restored.Fingerprint());
    }

    [Fact]
    public void A_snapshot_from_an_unknown_version_is_refused_rather_than_guessed()
    {
        var json = NewPaper().ToJson().Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => FieldDocument.FromJson(json));
    }

    [Fact]
    public void Review_levels_are_sequential_and_one_edit_breaks_every_standing_seal()
    {
        var doc = NewPaper();
        Set(doc, Preparer, "exceptions_noted", "0");
        doc.Apply(Guid.NewGuid(), Preparer, new SubmitForReview());

        Assert.Equal(Decision.Rejected, SignOff(doc, EngagementRole.JapanDesk, Director).Decision);   // Manager QA comes first
        Assert.Equal(Decision.Rejected, SignOff(doc, EngagementRole.ManagerQa, Preparer).Decision);   // not your own paper
        Assert.Equal(Decision.Rejected, SignOff(doc, EngagementRole.EngagementManager, Manager).Decision); // not a review level
        Assert.Equal(Decision.Accepted, SignOff(doc, EngagementRole.ManagerQa, Manager).Decision);
        Assert.Equal(Decision.Accepted, SignOff(doc, EngagementRole.JapanDesk, Director).Decision);
        Assert.Equal(WorkingPaperStatus.JapanDeskSignedOff, doc.Status);

        // Writing the value that is already there changes nothing and breaks nothing.
        var same = doc.Apply(Guid.NewGuid(), Preparer, new SetField("exceptions_noted", "0", null));
        Assert.Equal((Decision.Accepted, false), (same.Decision, same.SealBroken));

        var breaking = Guid.NewGuid();
        var result = doc.Apply(breaking, Preparer, new SetField("exceptions_noted", "1", doc.Fields["exceptions_noted"].ChangeId));

        Assert.Equal((Decision.Accepted, FieldDocument.SealBrokenReason), (result.Decision, result.Reason));
        Assert.Equal(new FieldChange("exceptions_noted", "0", "1"), result.Field);
        Assert.Equal([Manager, Director], result.BrokenSignOffs.Select(s => s.SignedBy));
        Assert.All(doc.SignOffs, s => Assert.Equal(breaking, s.BrokenByChangeId));
        Assert.Equal(WorkingPaperStatus.ReadyForReview, doc.Status);

        // A second edit has no standing seal left to break; review starts again at the first level.
        Assert.False(doc.Apply(Guid.NewGuid(), Preparer, new SetField("exceptions_noted", "2", breaking)).SealBroken);
        Assert.Equal(Decision.Rejected, SignOff(doc, EngagementRole.JapanDesk, Director).Decision);
        Assert.Equal(Decision.Accepted, SignOff(doc, EngagementRole.ManagerQa, Manager).Decision);
        Assert.Equal(3, doc.SignOffs.Count);
    }

    [Fact]
    public void Evidence_changes_break_a_seal_too_but_a_link_that_already_exists_does_not()
    {
        var doc = NewPaper();
        doc.Apply(Guid.NewGuid(), Preparer, new LinkEvidence("hash-a"));
        doc.Apply(Guid.NewGuid(), Preparer, new SubmitForReview());
        SignOff(doc, EngagementRole.ManagerQa, Manager);

        Assert.False(doc.Apply(Guid.NewGuid(), Preparer, new LinkEvidence("hash-a")).SealBroken);
        Assert.True(doc.Apply(Guid.NewGuid(), Preparer, new UnlinkEvidence("hash-a")).SealBroken);
    }

    [Fact]
    public void A_replayed_sign_off_whose_hash_does_not_match_the_content_is_refused()
    {
        var doc = NewPaper();
        Set(doc, Preparer, "exceptions_noted", "0");
        doc.Apply(Guid.NewGuid(), Preparer, new SubmitForReview());

        var result = doc.Apply(Guid.NewGuid(), Manager, new SignOffPaper(nameof(EngagementRole.ManagerQa), new string('0', 64)));

        Assert.Equal(Decision.Rejected, result.Decision);
        Assert.Contains("hash does not match", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Incomplete_operations_are_rejected_and_leave_no_trace()
    {
        var doc = NewPaper();
        var before = doc.Fingerprint();
        FieldOp[] incomplete =
        [
            new SetField("", "x", null), new SetField("key", null!, null), new SetField(new string('k', 201), "x", null),
            new SetField("bad\0key", "x", null), new LinkEvidence(" "), new RaiseNote(Guid.NewGuid(), "key", ""), new RespondNote(Guid.NewGuid(), "x"),
            new ResolveConflict(Guid.NewGuid(), "x", null), new CreatePaper("AUP-CSH-03", 1, Preparer),
        ];

        Assert.All(incomplete, op => Assert.Equal(Decision.Rejected, doc.Apply(Guid.NewGuid(), Preparer, op).Decision));
        Assert.Equal(before, doc.Fingerprint());
        Assert.Equal(Decision.Rejected, new FieldDocument(Guid.NewGuid(), Guid.NewGuid()).Apply(Guid.NewGuid(), Preparer, new SetField("k", "v", null)).Decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"op":"yrs-update","bytes":"AAAA"}""")]
    [InlineData("""{"op":"set","key":5}""")]
    public void A_payload_that_is_not_a_field_v1_operation_does_not_decode(string payload)
    {
        Assert.Throws<JsonException>(() => FieldPayload.Decode(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public void Every_operation_survives_the_wire_format()
    {
        FieldOp[] ops =
        [
            new CreatePaper("AUP-CSH-03", 2, Preparer), new SetField("k", "v", Guid.NewGuid()), new SetField("k", "v", null),
            new ResolveConflict(Guid.NewGuid(), "v", null), new LinkEvidence("h"), new UnlinkEvidence("h"),
            new RaiseNote(Guid.NewGuid(), "k", "t"), new RespondNote(Guid.NewGuid(), "r"), new ClearNote(Guid.NewGuid(), Guid.NewGuid()),
            new SubmitForReview(), new SignOffPaper("ManagerQa", "hash"),
        ];

        Assert.All(ops, op => Assert.Equal(op, FieldPayload.Decode(FieldPayload.Encode(op))));
        Assert.Equal(ops.Select(o => o.GetType()).Distinct().Count(), ops.Select(o => o.Name).Distinct().Count());
    }
}
