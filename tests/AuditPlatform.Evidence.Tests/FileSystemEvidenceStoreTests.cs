using System.Text;
using AuditPlatform.Evidence;

namespace AuditPlatform.Evidence.Tests;

public sealed class FileSystemEvidenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-evidence-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private static MemoryStream Content(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Hash_matches_published_blake3_vector()
    {
        // BLAKE3 of the empty input (official test vector).
        var (hash, size) = await EvidenceHash.ComputeAsync(new MemoryStream(), null, TestContext.Current.CancellationToken);
        Assert.Equal("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262", hash);
        Assert.Equal(0, size);
    }

    [Fact]
    public async Task Same_content_is_stored_once_with_same_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemEvidenceStore(_root);
        var a = await store.PutAsync(Content("bank confirmation"), ct);
        var b = await store.PutAsync(Content("bank confirmation"), ct);

        Assert.Equal(a, b);
        Assert.True(await store.ExistsAsync(a.Hash, ct));
        Assert.Single(Directory.EnumerateFiles(_root, a.Hash, SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, ".staging")));
    }

    [Fact]
    public async Task Reads_back_verified_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemEvidenceStore(_root);
        var r = await store.PutAsync(Content("cash count sheet"), ct);

        await using var s = await store.OpenVerifiedAsync(r.Hash, ct);
        using var reader = new StreamReader(s);
        Assert.Equal("cash count sheet", await reader.ReadToEndAsync(ct));
    }

    [Fact]
    public async Task Tampered_object_is_detected_on_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemEvidenceStore(_root);
        var r = await store.PutAsync(Content("invoice 0001"), ct);

        var path = Directory.EnumerateFiles(_root, r.Hash, SearchOption.AllDirectories).Single();
        File.SetAttributes(path, FileAttributes.Normal);
        await File.WriteAllTextAsync(path, "invoice 0002", ct);

        await Assert.ThrowsAsync<EvidenceIntegrityException>(async () => await store.OpenVerifiedAsync(r.Hash, ct));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF")]
    public async Task Rejects_non_hash_identifiers(string id)
    {
        var store = new FileSystemEvidenceStore(_root);
        Assert.False(await store.ExistsAsync(id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.OpenVerifiedAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Content_sent_under_the_wrong_hash_is_not_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemEvidenceStore(_root);
        var real = (await EvidenceHash.ComputeAsync(Content("cash count sheet"), null, ct)).Hash;
        var claimed = new string('0', 64);

        await Assert.ThrowsAsync<EvidenceIntegrityException>(async () => await store.PutAsync(Content("cash count sheet"), claimed, ct));
        Assert.False(await store.ExistsAsync(claimed, ct));
        Assert.False(await store.ExistsAsync(real, ct));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));

        Assert.Equal(real, (await store.PutAsync(Content("cash count sheet"), real, ct)).Hash);
        Assert.True(await store.ExistsAsync(real, ct));
    }
}
