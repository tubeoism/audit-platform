using AuditPlatform.Domain;
using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Domain.Tests;

public class EngagementTests
{
    private static readonly Guid Manager = Guid.NewGuid();

    private static Engagement NewEngagement()
    {
        var e = new Engagement(Guid.NewGuid(), "ENG-2026-001", "Client A", "AUP-2026-07", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        e.AddMember(Manager, EngagementRole.EngagementManager);
        return e;
    }

    private static void AdvanceToReporting(Engagement e)
    {
        e.MoveTo(EngagementStatus.Fieldwork);
        e.MoveTo(EngagementStatus.Review);
        e.MoveTo(EngagementStatus.Reporting);
    }

    [Fact]
    public void Follows_lifecycle_to_locked()
    {
        var e = NewEngagement();
        AdvanceToReporting(e);
        e.Lock(Manager, DateTimeOffset.UtcNow);

        Assert.Equal(EngagementStatus.Locked, e.Status);
        Assert.True(e.IsReadOnly);
        Assert.Equal(Manager, e.LockedBy);
    }

    [Fact]
    public void Cannot_skip_lifecycle_stages()
    {
        var e = NewEngagement();
        Assert.Throws<DomainException>(() => e.MoveTo(EngagementStatus.Reporting));
    }

    [Fact]
    public void Lock_requires_engagement_manager()
    {
        var e = NewEngagement();
        var preparer = Guid.NewGuid();
        e.AddMember(preparer, EngagementRole.Preparer);
        AdvanceToReporting(e);

        Assert.Throws<DomainException>(() => e.Lock(preparer, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Lock_cannot_be_reached_through_MoveTo()
    {
        var e = NewEngagement();
        AdvanceToReporting(e);
        Assert.Throws<DomainException>(() => e.MoveTo(EngagementStatus.Locked));
    }

    [Fact]
    public void Locked_engagement_rejects_writes()
    {
        var e = NewEngagement();
        AdvanceToReporting(e);
        e.Lock(Manager, DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => e.AddMember(Guid.NewGuid(), EngagementRole.Preparer));
        Assert.Throws<DomainException>(e.EnsureWritable);
    }

    [Fact]
    public void Rejects_inverted_period()
    {
        Assert.Throws<DomainException>(() =>
            new Engagement(Guid.NewGuid(), "X", "C", "R", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));
    }
}
