using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Sync.Tests;

public sealed class FieldPolicyTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static FieldDocument Paper()
    {
        var doc = new FieldDocument(Guid.NewGuid(), Guid.NewGuid());
        doc.Apply(Guid.NewGuid(), Owner, new CreatePaper("AUP-CSH-01", 1, Owner));
        return doc;
    }

    private static string? Refusal(FieldOp op, Guid author, params EngagementRole[] roles) =>
        FieldPolicy.WhyRefused(op, roles.ToHashSet(), Paper(), author);

    public static TheoryData<string, bool, bool, bool, bool> Matrix => new()
    {
        // operation,   Preparer, ManagerQa, JapanDesk, EngagementManager
        { "set",        true,     false,     false,     true },
        { "link",       true,     false,     false,     true },
        { "unlink",     true,     false,     false,     true },
        { "submit",     true,     false,     false,     true },
        { "respond",    true,     false,     false,     true },
        { "raise",      false,    true,      true,      true },
        { "clear",      false,    true,      true,      true },
    };

    [Theory, MemberData(nameof(Matrix))]
    public void Each_role_may_do_what_it_is_for(string name, bool preparer, bool managerQa, bool japanDesk, bool engagementManager)
    {
        FieldOp op = name switch
        {
            "set" => new SetField("k", "v", null),
            "link" => new LinkEvidence("h"),
            "unlink" => new UnlinkEvidence("h"),
            "submit" => new SubmitForReview(),
            "respond" => new RespondNote(Guid.NewGuid(), "r"),
            "raise" => new RaiseNote(Guid.NewGuid(), "k", "t"),
            _ => new ClearNote(Guid.NewGuid(), Guid.NewGuid()),
        };

        Assert.Equal(preparer, Refusal(op, Other, EngagementRole.Preparer) is null);
        Assert.Equal(managerQa, Refusal(op, Other, EngagementRole.ManagerQa) is null);
        Assert.Equal(japanDesk, Refusal(op, Other, EngagementRole.JapanDesk) is null);
        Assert.Equal(engagementManager, Refusal(op, Other, EngagementRole.EngagementManager) is null);
        Assert.NotNull(Refusal(op, Other));
    }

    [Fact]
    public void A_conflict_is_decided_by_the_papers_preparer_or_the_engagement_manager()
    {
        var resolve = new ResolveConflict(Guid.NewGuid(), "v", null);
        Assert.Null(Refusal(resolve, Owner, EngagementRole.Preparer));
        Assert.Null(Refusal(resolve, Other, EngagementRole.EngagementManager));
        Assert.Equal(FieldPolicy.NotTheOwner, Refusal(resolve, Other, EngagementRole.Preparer));
        Assert.Equal(FieldPolicy.NotTheOwner, Refusal(resolve, Other, EngagementRole.ManagerQa, EngagementRole.JapanDesk));
        // Someone who prepared the paper but no longer holds the preparer role has no say either.
        Assert.Equal(FieldPolicy.NotTheOwner, Refusal(resolve, Owner, EngagementRole.ManagerQa));
    }

    [Fact]
    public void A_paper_is_created_by_its_preparer_or_assigned_by_the_engagement_manager()
    {
        Assert.Null(Refusal(new CreatePaper("AUP-CSH-02", 1, Other), Other, EngagementRole.Preparer));
        Assert.Equal(FieldPolicy.NotYourPaper, Refusal(new CreatePaper("AUP-CSH-02", 1, Owner), Other, EngagementRole.Preparer));
        Assert.Null(Refusal(new CreatePaper("AUP-CSH-02", 1, Owner), Other, EngagementRole.EngagementManager));
        Assert.Equal(FieldPolicy.NotAnEditor, Refusal(new CreatePaper("AUP-CSH-02", 1, Other), Other, EngagementRole.JapanDesk));
    }
}
