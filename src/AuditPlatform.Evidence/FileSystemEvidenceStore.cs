namespace AuditPlatform.Evidence;

/// <summary>
/// Stores objects at <c>{root}/{h[0..2]}/{h[2..4]}/{h}</c>. Writes go to a temporary file first and are moved into
/// place only after hashing, so a partially written file is never visible under a valid hash.
/// </summary>
public sealed class FileSystemEvidenceStore : IEvidenceStore
{
    private readonly string _root;
    private readonly string _staging;

    public FileSystemEvidenceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(_staging);
    }

    public ValueTask<EvidenceRef> PutAsync(Stream content, CancellationToken cancellationToken) => PutCoreAsync(content, null, cancellationToken);

    public ValueTask<EvidenceRef> PutAsync(Stream content, string expectedHash, CancellationToken cancellationToken)
    {
        if (!EvidenceHash.IsValid(expectedHash))
        {
            throw new ArgumentException("Not a BLAKE3-256 hex hash.", nameof(expectedHash));
        }

        return PutCoreAsync(content, expectedHash, cancellationToken);
    }

    private async ValueTask<EvidenceRef> PutCoreAsync(Stream content, string? expectedHash, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
        string hash;
        long size;
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                (hash, size) = await EvidenceHash.ComputeAsync(content, temp, cancellationToken);
                await temp.FlushAsync(cancellationToken);
            }

            if (expectedHash is not null && !string.Equals(hash, expectedHash, StringComparison.Ordinal))
            {
                throw new EvidenceIntegrityException($"The content received hashes to {hash}, not to {expectedHash}.");
            }

            var finalPath = PathFor(hash);
            if (File.Exists(finalPath))
            {
                return new EvidenceRef(hash, size);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                File.Move(tempPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Another writer stored identical content first; same hash means same bytes.
            }

            File.SetAttributes(finalPath, FileAttributes.ReadOnly);
            return new EvidenceRef(hash, size);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public ValueTask<bool> ExistsAsync(string hash, CancellationToken cancellationToken) =>
        ValueTask.FromResult(EvidenceHash.IsValid(hash) && File.Exists(PathFor(hash)));

    public async ValueTask<Stream> OpenVerifiedAsync(string hash, CancellationToken cancellationToken)
    {
        if (!EvidenceHash.IsValid(hash))
        {
            throw new ArgumentException("Not a BLAKE3-256 hex hash.", nameof(hash));
        }

        var path = PathFor(hash);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            var (actual, _) = await EvidenceHash.ComputeAsync(stream, null, cancellationToken);
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                throw new EvidenceIntegrityException($"Evidence object {hash} has been altered (content hash {actual}).");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private string PathFor(string hash) => Path.Combine(_root, hash[..2], hash[2..4], hash);
}
