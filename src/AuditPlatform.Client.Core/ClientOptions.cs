namespace AuditPlatform.Client.Core;

public sealed class ClientOptions
{
    public const string Section = "Client";

    /// <summary>Master endpoint. Production: https://app.audit.io.vn (TLS terminated on the Oracle VM).</summary>
    public Uri MasterUrl { get; set; } = new("https://app.audit.io.vn");

    /// <summary>Local data root (SQLite database and evidence cache). Must sit on a BitLocker-encrypted volume.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuditPlatform");

    /// <summary>Stable id of this installation, written on first run.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Language of the procedure wording on the forms: "vi" or "en". The other one is used when a text is missing.</summary>
    public string TemplateLanguage { get; set; } = "vi";

    /// <summary>How often the client synchronises when nothing else triggers it (a signal from the master, a local save).</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(60);

    public string DatabasePath => Path.Combine(DataDirectory, "client.db");

    /// <summary>Largest evidence file this client attaches; the master enforces its own limit.</summary>
    public long MaxEvidenceBytes { get; set; } = AuditPlatform.Contracts.Evidence.EvidenceRoutes.DefaultMaxPartedBytes;

    /// <summary>A file larger than this is sent in parts, so that no single request exceeds what the proxy in front of the master takes.</summary>
    public long PartedUploadThreshold { get; set; } = AuditPlatform.Contracts.Evidence.EvidenceRoutes.DefaultMaxBytes;

    public string EvidenceCachePath => Path.Combine(DataDirectory, "evidence");

    /// <summary>Copies of evidence files under their own names, made when a file is opened; emptied at start-up.</summary>
    public string EvidenceViewPath => Path.Combine(DataDirectory, "evidence-view");
}
