namespace AuditPlatform.Master;

public sealed class MasterOptions
{
    public const string Section = "Master";

    /// <summary>Root directory of the content-addressed evidence store on the VM (block volume), e.g. /srv/audit/evidence.</summary>
    public string EvidenceRoot { get; set; } = "/srv/audit/evidence";

    /// <summary>
    /// Largest evidence file accepted, in bytes. The default stays under the 100 MB request limit of the Cloudflare
    /// proxy in front of the master; a larger file needs an upload in parts, which does not exist yet.
    /// </summary>
    /// <summary>Size of the parts a large evidence file is sent in; below the request limit of the proxy in front of the master.</summary>
    public long EvidencePartBytes { get; set; } = AuditPlatform.Contracts.Evidence.EvidenceRoutes.DefaultPartBytes;

    /// <summary>Largest evidence file taken in parts.</summary>
    public long MaxPartedEvidenceBytes { get; set; } = AuditPlatform.Contracts.Evidence.EvidenceRoutes.DefaultMaxPartedBytes;

    public long MaxEvidenceBytes { get; set; } = AuditPlatform.Contracts.Evidence.EvidenceRoutes.DefaultMaxBytes;

    /// <summary>Upper bound on changes returned by one pull.</summary>
    public int MaxPullBatch { get; set; } = 500;

    /// <summary>Upper bound on changes accepted in one push; the client sends 200 at a time.</summary>
    public int MaxPushBatch { get; set; } = 500;

    /// <summary>Sends pending notifications (e.g. broken seals) by e-mail in the background. Off in tests, which send on demand.</summary>
    public bool RunNotificationDispatcher { get; set; } = true;
}
