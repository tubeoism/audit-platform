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

    public string DatabasePath => Path.Combine(DataDirectory, "client.db");

    public string EvidenceCachePath => Path.Combine(DataDirectory, "evidence");
}
