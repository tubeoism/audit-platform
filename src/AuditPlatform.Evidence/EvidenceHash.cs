using Blake3;

namespace AuditPlatform.Evidence;

public static class EvidenceHash
{
    public const int HexLength = 64;

    public static async ValueTask<(string Hash, long Size)> ComputeAsync(Stream content, Stream? copyTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var hasher = Hasher.New();
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
            if (copyTo is not null)
            {
                await copyTo.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            size += read;
        }

        return (hasher.Finalize().ToString(), size);
    }

    public static bool IsValid(string hash) =>
        hash is { Length: HexLength } && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
