namespace AuditPlatform.Master;

public sealed class MasterOptions
{
    public const string Section = "Master";

    /// <summary>Root directory of the content-addressed evidence store on the VM (block volume), e.g. /srv/audit/evidence.</summary>
    public string EvidenceRoot { get; set; } = "/srv/audit/evidence";

    /// <summary>Upper bound on changes returned by one pull.</summary>
    public int MaxPullBatch { get; set; } = 500;
}
