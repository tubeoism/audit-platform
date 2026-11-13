namespace AuditPlatform.Master;

public sealed class MasterOptions
{
    public const string Section = "Master";

    /// <summary>Root directory of the content-addressed evidence store on the VM (block volume), e.g. /srv/audit/evidence.</summary>
    public string EvidenceRoot { get; set; } = "/srv/audit/evidence";

    /// <summary>Upper bound on changes returned by one pull.</summary>
    public int MaxPullBatch { get; set; } = 500;

    /// <summary>Upper bound on changes accepted in one push; the client sends 200 at a time.</summary>
    public int MaxPushBatch { get; set; } = 500;

    /// <summary>Sends pending notifications (e.g. broken seals) by e-mail in the background. Off in tests, which send on demand.</summary>
    public bool RunNotificationDispatcher { get; set; } = true;
}
