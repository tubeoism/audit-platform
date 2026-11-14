using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using AuditPlatform.Master.Archive;

namespace AuditPlatform.Master.Tests.Archive;

public sealed class ArchiveTarTests
{
    private static readonly DateTimeOffset Modified = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private const string Root = "engagement-test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // The store addresses files by BLAKE3; the archive checks them by SHA-256. Any name will do for the test.
    private static ArchiveEntry Evidence(byte[] content, string? name = null) =>
        ArchiveEntry.FromStream($"evidence/files/{name ?? Sha(content)}",
            _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false)));

    private static List<ArchiveEntry> Sample(byte[]? file = null) =>
    [
        ArchiveEntry.FromBytes("manifest.json", Encoding.UTF8.GetBytes("{\"formatVersion\":1}")),
        ArchiveEntry.FromBytes("papers/a.json", Encoding.UTF8.GetBytes("{\"x\":1}")),
        Evidence(file ?? Encoding.UTF8.GetBytes("scan of the bank confirmation")),
    ];

    private static async Task<(byte[] Bytes, ArchiveTarResult Result, ArchiveVerification Check)> BuildAsync(IReadOnlyList<ArchiveEntry> entries)
    {
        using var buffer = new MemoryStream();
        var result = await ArchiveTar.WriteAsync(buffer, Root, entries, Modified, Ct);
        buffer.Position = 0;
        var check = await ArchiveTar.VerifyAsync(buffer, Ct);
        return (buffer.ToArray(), result, check);
    }

    private static async Task<Dictionary<string, byte[]>> ReadAsync(byte[] tar)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var reader = new TarReader(new MemoryStream(tar));
        while (await reader.GetNextEntryAsync(cancellationToken: Ct) is { } entry)
        {
            using var copy = new MemoryStream();
            await entry.DataStream!.CopyToAsync(copy, Ct);
            files[entry.Name] = copy.ToArray();
        }

        return files;
    }

    [Fact]
    public async Task Archive_holds_every_file_under_one_root_and_ends_with_the_checksum_list()
    {
        var (tar, result, _) = await BuildAsync(Sample());

        var files = await ReadAsync(tar);
        Assert.Equal(4, result.Files);
        Assert.Equal(4, files.Count);
        Assert.All(files.Keys, name => Assert.StartsWith(Root + "/", name, StringComparison.Ordinal));
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(files[$"{Root}/papers/a.json"]));

        await using var reader = new TarReader(new MemoryStream(tar));
        string? last = null;
        while (await reader.GetNextEntryAsync(cancellationToken: Ct) is { } entry)
        {
            last = entry.Name;
        }

        Assert.Equal($"{Root}/{ArchiveTar.SumsFile}", last);
    }

    [Fact]
    public async Task Checksum_list_matches_what_sha256sum_would_say_and_the_digest_names_the_list()
    {
        var (tar, result, check) = await BuildAsync(Sample());
        var files = await ReadAsync(tar);

        var sums = Encoding.UTF8.GetString(files[$"{Root}/{ArchiveTar.SumsFile}"]);
        Assert.Equal(result.Sha256Sums, sums);
        foreach (var line in sums.TrimEnd('\n').Split('\n'))
        {
            var parts = line.Split("  ", 2);
            Assert.Equal(parts[0], Sha(files[parts[1]]));
        }

        Assert.Equal(Sha(Encoding.UTF8.GetBytes(sums)), result.PackageDigest);
        Assert.Equal(Sha(tar), check.TarSha256);
        Assert.Equal(tar.Length, check.Bytes);
    }

    [Fact]
    public async Task Same_entries_give_the_same_bytes_whatever_order_they_are_given_in()
    {
        var (first, a, aCheck) = await BuildAsync(Sample());
        var reversed = Sample();
        reversed.Reverse();
        var (second, b, bCheck) = await BuildAsync(reversed);

        Assert.Equal(first, second);
        Assert.Equal(aCheck.TarSha256, bCheck.TarSha256);
        Assert.Equal(a.PackageDigest, b.PackageDigest);
    }

    [Fact]
    public async Task A_change_in_any_file_changes_the_package_digest()
    {
        var (_, original, originalCheck) = await BuildAsync(Sample());
        var (_, changed, changedCheck) = await BuildAsync(Sample(Encoding.UTF8.GetBytes("scan of the bank confirmation, edited")));

        Assert.NotEqual(original.PackageDigest, changed.PackageDigest);
        Assert.NotEqual(originalCheck.TarSha256, changedCheck.TarSha256);
    }

    /// <summary>Reads as <c>first</c> until it is rewound, then as <c>second</c>: a file that changed between hashing and copying.</summary>
    private sealed class ChangingStream(byte[] first, byte[] second) : Stream
    {
        private byte[] _data = first;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set
            {
                _position = value;
                _data = second;
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Evidence_is_listed_by_its_real_sha256_whatever_it_is_called()
    {
        var content = Encoding.UTF8.GetBytes("a file addressed by BLAKE3 elsewhere");
        var blake3Name = new string('b', 64);

        var (tar, result, _) = await BuildAsync([ArchiveEntry.FromBytes("manifest.json", [1]), Evidence(content, blake3Name)]);

        Assert.Contains($"{Sha(content)}  {Root}/evidence/files/{blake3Name}\n", result.Sha256Sums, StringComparison.Ordinal);
        Assert.Equal(content, (await ReadAsync(tar))[$"{Root}/evidence/files/{blake3Name}"]);
    }

    [Fact]
    public async Task A_file_that_changes_between_hashing_and_copying_is_caught_when_the_written_archive_is_checked()
    {
        var changing = ArchiveEntry.FromStream("evidence/files/x",
            _ => ValueTask.FromResult<Stream>(new ChangingStream(Encoding.UTF8.GetBytes("version one"), Encoding.UTF8.GetBytes("version TWO"))));

        using var buffer = new MemoryStream();
        await ArchiveTar.WriteAsync(buffer, Root, [ArchiveEntry.FromBytes("manifest.json", [1]), changing], Modified, Ct);
        buffer.Position = 0;
        var thrown = await Assert.ThrowsAsync<ArchiveIntegrityException>(() => ArchiveTar.VerifyAsync(buffer, Ct));

        Assert.Contains("evidence/files/x", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_that_cannot_seek_is_refused()
    {
        var forward = ArchiveEntry.FromStream("evidence/files/x", _ => ValueTask.FromResult<Stream>(new ForwardOnlyStream([1, 2, 3])));

        using var buffer = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => ArchiveTar.WriteAsync(buffer, Root, [forward], Modified, Ct));
    }

    [Fact]
    public async Task A_byte_changed_after_writing_is_caught()
    {
        var (tar, _, _) = await BuildAsync(Sample());
        var tampered = (byte[])tar.Clone();
        var at = Encoding.UTF8.GetString(tampered).IndexOf("{\"x\":1}", StringComparison.Ordinal);
        tampered[at + 5] = (byte)'2';

        await Assert.ThrowsAsync<ArchiveIntegrityException>(() => ArchiveTar.VerifyAsync(new MemoryStream(tampered), Ct));
    }

    [Fact]
    public async Task An_archive_without_its_checksum_list_is_refused()
    {
        using var buffer = new MemoryStream();
        await using (var tar = new System.Formats.Tar.TarWriter(buffer, System.Formats.Tar.TarEntryFormat.Ustar, leaveOpen: true))
        {
            await tar.WriteEntryAsync(new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "x/a") { DataStream = new MemoryStream([1]) }, Ct);
        }

        buffer.Position = 0;
        await Assert.ThrowsAsync<ArchiveIntegrityException>(() => ArchiveTar.VerifyAsync(buffer, Ct));
    }

    [Fact]
    public async Task Large_evidence_is_streamed_and_still_verified()
    {
        var big = new byte[3 * 1024 * 1024 + 17];
        RandomNumberGenerator.Fill(big);

        var (tar, _, _) = await BuildAsync([ArchiveEntry.FromBytes("manifest.json", [1]), Evidence(big)]);

        var files = await ReadAsync(tar);
        Assert.Equal(big, files[$"{Root}/evidence/files/{Sha(big)}"]);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/absolute.json")]
    [InlineData("a//b.json")]
    [InlineData("a/./b.json")]
    [InlineData("a\\b.json")]
    [InlineData("")]
    public async Task Unsafe_paths_are_refused(string path)
    {
        using var buffer = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArchiveTar.WriteAsync(buffer, Root, [ArchiveEntry.FromBytes(path, [1])], Modified, Ct));
    }

    [Fact]
    public async Task Duplicate_paths_and_a_hand_made_checksum_file_are_refused()
    {
        using var buffer = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArchiveTar.WriteAsync(buffer, Root, [ArchiveEntry.FromBytes("a", [1]), ArchiveEntry.FromBytes("a", [2])], Modified, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArchiveTar.WriteAsync(buffer, Root, [ArchiveEntry.FromBytes(ArchiveTar.SumsFile, [1])], Modified, Ct));
    }
}
