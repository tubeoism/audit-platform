using AuditPlatform.Contracts.Evidence;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Evidence;

/// <summary>
/// Large evidence files arrive in parts so that no request exceeds what the proxy in front of the master takes. Parts
/// wait under <c>.staging/parts</c> of the evidence root (which the mirror skips), keyed by engagement and content hash;
/// a part is written whole or not at all, so a broken connection loses at most the part in flight and the sender
/// resumes from what is listed. Completing assembles the parts in order and hands them to the ordinary intake, which
/// checks the whole file against its hash; nothing reaches the store, the database or the audit log before that.
/// </summary>
public sealed class EvidenceUploads(EvidenceService evidence, IOptions<MasterOptions> options)
{
    private static readonly TimeSpan Stale = TimeSpan.FromDays(7);
    private static long _lastSweep;

    private long PartBytes => Math.Max(1, options.Value.EvidencePartBytes);

    public async Task<(EvidenceResult? Refusal, EvidenceParts? Parts)> StatusAsync(
        Guid engagementId, Guid userId, string hash, long total, CancellationToken cancellationToken)
    {
        if (await evidence.CheckCanUploadAsync(engagementId, userId, cancellationToken) is { } refusal)
        {
            return (refusal, null);
        }

        if (Check(total) is { } wrong)
        {
            return (wrong, null);
        }

        var received = new List<int>();
        var folder = Folder(engagementId, hash);
        if (Directory.Exists(folder))
        {
            for (var number = 1; number <= Count(total); number++)
            {
                if (new FileInfo(PartPath(folder, number)) is { Exists: true } file && file.Length == Expected(number, total))
                {
                    received.Add(number);
                }
            }
        }

        return (null, new EvidenceParts(PartBytes, options.Value.MaxPartedEvidenceBytes, received));
    }

    public async Task<EvidenceResult> ReceivePartAsync(
        Guid engagementId, Guid userId, string hash, int number, long total, long declaredLength, Stream body, CancellationToken cancellationToken)
    {
        if (await evidence.CheckCanUploadAsync(engagementId, userId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (Check(total) is { } wrong)
        {
            return wrong;
        }

        if (number < 1 || number > Count(total) || declaredLength != Expected(number, total))
        {
            return new EvidenceResult(EvidenceOutcome.BadRequest,
                $"Part {number} of a {total}-byte file must be {(number >= 1 && number <= Count(total) ? Expected(number, total) : 0)} bytes.");
        }

        Sweep();
        var folder = Folder(engagementId, hash);
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await body.CopyToAsync(file, cancellationToken);
                if (file.Length != declaredLength)
                {
                    return new EvidenceResult(EvidenceOutcome.BadRequest, "The part is shorter than declared.");
                }
            }

            File.Move(temp, PartPath(folder, number), overwrite: true);
            return new EvidenceResult(EvidenceOutcome.Ok);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task<EvidenceResult> CompleteAsync(
        Guid engagementId, Guid userId, string hash, long total, string fileName, string? mediaType, CancellationToken cancellationToken)
    {
        if (await evidence.CheckCanUploadAsync(engagementId, userId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (Check(total) is { } wrong)
        {
            return wrong;
        }

        var folder = Folder(engagementId, hash);
        var files = Enumerable.Range(1, Count(total)).Select(n => PartPath(folder, n)).ToList();
        if (!Directory.Exists(folder) || files.Where((path, i) => !File.Exists(path) || new FileInfo(path).Length != Expected(i + 1, total)).Any())
        {
            return new EvidenceResult(EvidenceOutcome.Incomplete, "Not every part of the file has arrived.");
        }

        EvidenceResult result;
        await using (var assembled = new PartsStream(files))
        {
            result = await evidence.ReceiveAsync(engagementId, userId, hash, fileName, mediaType, assembled, cancellationToken);
        }

        // Taken, or wrong beyond repair (the parts do not make the file the hash names): either way the parts are done with.
        if (result.Outcome is EvidenceOutcome.Ok or EvidenceOutcome.HashMismatch)
        {
            Directory.Delete(folder, recursive: true);
        }

        return result;
    }

    private EvidenceResult? Check(long total) =>
        total <= 0 || total > options.Value.MaxPartedEvidenceBytes
            ? new EvidenceResult(EvidenceOutcome.BadRequest, $"A file sent in parts may be at most {options.Value.MaxPartedEvidenceBytes / (1024.0 * 1024):0.##} MB.")
            : null;

    private int Count(long total) => (int)((total + PartBytes - 1) / PartBytes);

    private long Expected(int number, long total) => number < Count(total) ? PartBytes : total - ((number - 1) * PartBytes);

    private string Folder(Guid engagementId, string hash) =>
        Path.Combine(Path.GetFullPath(options.Value.EvidenceRoot), ".staging", "parts", engagementId.ToString("N"), hash);

    private static string PartPath(string folder, int number) => Path.Combine(folder, $"{number:D6}.part");

    /// <summary>Removes uploads nobody continued for a week; at most once an hour.</summary>
    private void Sweep()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSweep);
        if (now - last < TimeSpan.FromHours(1).TotalMilliseconds && last != 0
            || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        var root = Path.Combine(Path.GetFullPath(options.Value.EvidenceRoot), ".staging", "parts");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var upload in Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(upload) > Stale)
                {
                    Directory.Delete(upload, recursive: true);
                }
            }
            catch (IOException)
            {
                // In use right now; the next sweep looks again.
            }
        }
    }

    /// <summary>The parts one after the other as a single readable stream.</summary>
    private sealed class PartsStream(IReadOnlyList<string> paths) : Stream
    {
        private int _index;
        private FileStream? _current;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (_index >= paths.Count)
                    {
                        return 0;
                    }

                    _current = new FileStream(paths[_index++], FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
                }

                var read = _current.Read(buffer);
                if (read > 0)
                {
                    return read;
                }

                _current.Dispose();
                _current = null;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (_index >= paths.Count)
                    {
                        return 0;
                    }

                    _current = new FileStream(paths[_index++], FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                }

                var read = await _current.ReadAsync(buffer, cancellationToken);
                if (read > 0)
                {
                    return read;
                }

                await _current.DisposeAsync();
                _current = null;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
