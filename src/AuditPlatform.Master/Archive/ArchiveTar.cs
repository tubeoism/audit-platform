using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace AuditPlatform.Master.Archive;

/// <summary>
/// One file of the archive: either bytes held in memory, or an evidence file that is opened only when it is written,
/// so a multi-gigabyte engagement is never held in memory. A stream must be seekable: it is read once to take its
/// SHA-256 (the evidence store's own address is BLAKE3, which <c>sha256sum</c> cannot check) and once more to be written.
/// <see cref="ArchiveTar.VerifyAsync"/> then checks what really went into the file against the list.
/// </summary>
internal sealed record ArchiveEntry(string Path, byte[]? Bytes, Func<CancellationToken, ValueTask<Stream>>? Open)
{
    public static ArchiveEntry FromBytes(string path, byte[] bytes) => new(path, bytes, null);

    public static ArchiveEntry FromStream(string path, Func<CancellationToken, ValueTask<Stream>> open) => new(path, null, open);
}

/// <param name="Sha256Sums">Text of the <c>SHA256SUMS</c> file at the end of the archive.</param>
/// <param name="PackageDigest">SHA-256 of <c>SHA256SUMS</c>: names every file's content, so one value identifies the whole package.</param>
/// <param name="Files">Number of files in the archive, <c>SHA256SUMS</c> included.</param>
internal sealed record ArchiveTarResult(string Sha256Sums, string PackageDigest, int Files);

/// <param name="TarSha256">SHA-256 of the whole tar file; what a copy of the file is compared against after transfer.</param>
internal sealed record ArchiveVerification(string TarSha256, long Bytes, int Files);

/// <summary>What was written to the archive is not what its checksum list says.</summary>
internal sealed class ArchiveIntegrityException(string message) : Exception(message);

/// <summary>
/// Writes the archive as one POSIX tar. The same entries and the same <c>modified</c> time give the same bytes: entries
/// are sorted, owners and modes are fixed, and the last entry is <c>SHA256SUMS</c> naming every other file, so the
/// package can be checked with <c>sha256sum -c</c> on any machine, with no tool of ours.
/// </summary>
internal static class ArchiveTar
{
    public const string SumsFile = "SHA256SUMS";

    /// <summary>Writes the entries to <paramref name="destination"/>, which must be seekable (a file).</summary>
    public static async Task<ArchiveTarResult> WriteAsync(
        Stream destination, string root, IReadOnlyList<ArchiveEntry> entries, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(entries);
        ValidateRoot(root);
        var ordered = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        foreach (var entry in ordered)
        {
            ValidatePath(entry.Path);
        }

        if (ordered.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count() != ordered.Count)
        {
            throw new ArgumentException("Two entries have the same path.", nameof(entries));
        }

        if (ordered.Any(e => e.Path.Split('/')[^1] == SumsFile))
        {
            throw new ArgumentException($"{SumsFile} is written by the archive itself; no entry may carry that name.", nameof(entries));
        }

        var sums = new List<(string Hash, string Path)>();
        await using (var tar = new TarWriter(destination, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var entry in ordered)
            {
                if (entry.Bytes is { } bytes)
                {
                    sums.Add((Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Path));
                    await tar.WriteEntryAsync(NewEntry($"{root}/{entry.Path}", new MemoryStream(bytes, writable: false), modified), cancellationToken);
                }
                else
                {
                    await using var source = await entry.Open!(cancellationToken);
                    if (!source.CanSeek)
                    {
                        throw new ArgumentException($"{entry.Path}: the stream must be seekable.", nameof(entries));
                    }

                    sums.Add((await Sha256Async(source, cancellationToken), entry.Path));
                    source.Position = 0;
                    await tar.WriteEntryAsync(NewEntry($"{root}/{entry.Path}", source, modified), cancellationToken);
                }
            }

            var text = SumsText(root, sums);
            await tar.WriteEntryAsync(NewEntry($"{root}/{SumsFile}", new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false), modified), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        var sumsText = SumsText(root, sums);
        return new ArchiveTarResult(sumsText, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sumsText))), ordered.Count + 1);
    }

    /// <summary>
    /// Reads the tar back from the start and checks every file against <c>SHA256SUMS</c>, which must list exactly the
    /// files present. This is the check of what was really written, not of what we meant to write; it also hashes the
    /// whole tar file in the same pass.
    /// </summary>
    public static async Task<ArchiveVerification> VerifyAsync(Stream tar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tar);
        await using var reading = new HashingReadStream(tar);
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? sumsText = null;
        await using (var reader = new TarReader(reading, leaveOpen: true))
        {
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                {
                    throw new ArchiveIntegrityException($"{entry.Name}: not a regular file.");
                }

                if (entry.Name.EndsWith("/" + SumsFile, StringComparison.Ordinal))
                {
                    using var text = new MemoryStream();
                    await entry.DataStream.CopyToAsync(text, cancellationToken);
                    sumsText = Encoding.UTF8.GetString(text.ToArray());
                    continue;
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await entry.DataStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                actual[entry.Name] = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
        }

        // Padding and the end-of-archive blocks belong to the file hash too.
        await reading.DrainAsync(cancellationToken);

        if (sumsText is null)
        {
            throw new ArchiveIntegrityException($"{SumsFile} is missing.");
        }

        var listed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in sumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split("  ", 2);
            if (parts.Length != 2 || !listed.TryAdd(parts[1], parts[0]))
            {
                throw new ArchiveIntegrityException($"{SumsFile} has a malformed or repeated line: '{line}'.");
            }
        }

        foreach (var (name, hash) in actual)
        {
            if (!listed.TryGetValue(name, out var expected))
            {
                throw new ArchiveIntegrityException($"{name} is in the archive but not in {SumsFile}.");
            }

            if (!string.Equals(expected, hash, StringComparison.Ordinal))
            {
                throw new ArchiveIntegrityException($"{name}: written {hash}, {SumsFile} says {expected}.");
            }
        }

        if (listed.Keys.FirstOrDefault(name => !actual.ContainsKey(name)) is { } absent)
        {
            throw new ArchiveIntegrityException($"{absent} is listed in {SumsFile} but is not in the archive.");
        }

        return new ArchiveVerification(reading.Sha256Hex(), reading.Count, actual.Count + 1);
    }

    private static async Task<string> Sha256Async(Stream source, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string SumsText(string root, IEnumerable<(string Hash, string Path)> sums) =>
        string.Concat(sums.Select(s => $"{s.Hash}  {root}/{s.Path}\n"));

    private static UstarTarEntry NewEntry(string name, Stream data, DateTimeOffset modified) =>
        new(TarEntryType.RegularFile, name)
        {
            DataStream = data,
            ModificationTime = modified,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            Uid = 0,
            Gid = 0,
            UserName = string.Empty,
            GroupName = string.Empty,
        };

    private static void ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Contains('/', StringComparison.Ordinal) || root is "." or "..")
        {
            throw new ArgumentException("The root must be one plain folder name.", nameof(root));
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal)
            || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException($"Unsafe archive path: '{path}'.", nameof(path));
        }
    }

    /// <summary>A forward-only read stream that hashes and counts every byte passing through, leaving the inner stream open.</summary>
    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Count { get; private set; }

        public string Sha256Hex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            while (await ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            Track(buffer[..n]);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            Track(buffer.Span[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Track(ReadOnlySpan<byte> data)
        {
            if (data.Length > 0)
            {
                _hash.AppendData(data);
                Count += data.Length;
            }
        }
    }
}
