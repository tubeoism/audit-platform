namespace AuditPlatform.Evidence;

/// <summary>
/// Immutable, content-addressed blob store. Writing the same bytes twice yields the same reference and stores one copy.
/// The master uses a filesystem store on the VM, mirrored to Cloudflare R2; clients cache objects for assigned engagements.
/// </summary>
public interface IEvidenceStore
{
    ValueTask<EvidenceRef> PutAsync(Stream content, CancellationToken cancellationToken);

    ValueTask<bool> ExistsAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Opens the object for reading after verifying that its content still matches its hash.</summary>
    ValueTask<Stream> OpenVerifiedAsync(string hash, CancellationToken cancellationToken);
}
