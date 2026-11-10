namespace AuditPlatform.Domain.Engagements;

/// <summary>Roles within one engagement. Review levels follow sponsor answer Q8.</summary>
public enum EngagementRole
{
    /// <summary>Performs procedures and prepares working papers.</summary>
    Preparer = 0,

    /// <summary>First review level: Manager / Director in charge (QA).</summary>
    ManagerQa = 1,

    /// <summary>Second review level: Japan Desk, reporting to Japanese clients.</summary>
    JapanDesk = 2,

    /// <summary>Engagement owner: may request lock; holds a full replica for failover.</summary>
    EngagementManager = 3,
}
