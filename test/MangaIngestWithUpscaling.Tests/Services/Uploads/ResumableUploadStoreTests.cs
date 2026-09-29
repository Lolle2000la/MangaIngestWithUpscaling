using System.Security.Cryptography;
using System.Text;
using MangaIngestWithUpscaling.Services.Uploads;

namespace MangaIngestWithUpscaling.Tests.Services.Uploads;

public class ResumableUploadStoreTests : IDisposable
{
    private const string Identity = "sha256:aaaa";
    private const string OtherIdentity = "sha256:bbbb";

    private readonly string _root;
    private readonly ResumableUploadStore _store;

    public ResumableUploadStoreTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "resumable_upload_test_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_root);
        _store = new ResumableUploadStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static async Task<string> AssembleAndHashAsync(
        ResumableUploadStore store,
        int taskId,
        int totalChunks
    )
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using MemoryStream assembled = new();
        await store.AssembleAsync(taskId, totalChunks, assembled, hash, CancellationToken.None);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetContiguousChunkCount_StopsAtFirstGap()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("a"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(1, 1, Bytes("b"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(1, 3, Bytes("d"), Identity, CancellationToken.None);

        Assert.Equal(2, _store.GetContiguousChunkCount(1, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetContiguousChunkCount_IsolatedPerTask()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("a"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(12, 0, Bytes("x"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(12, 1, Bytes("y"), Identity, CancellationToken.None);

        Assert.Equal(1, _store.GetContiguousChunkCount(1, Identity));
        Assert.Equal(2, _store.GetContiguousChunkCount(12, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetContiguousChunkCount_NoChunks_ReturnsZero()
    {
        Assert.Equal(0, _store.GetContiguousChunkCount(42, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteChunkAsync_OverwritesExistingChunk()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("old"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(1, 0, Bytes("new"), Identity, CancellationToken.None);

        Assert.Equal(1, _store.GetContiguousChunkCount(1, Identity));

        await using MemoryStream assembled = new();
        await _store.AssembleAsync(1, 1, assembled, null, CancellationToken.None);
        Assert.Equal("new", Encoding.UTF8.GetString(assembled.ToArray()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AssembleAsync_ConcatenatesChunksInOrder()
    {
        await _store.WriteChunkAsync(7, 0, Bytes("Hello "), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(7, 1, Bytes("resumable "), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(7, 2, Bytes("world"), Identity, CancellationToken.None);

        await using MemoryStream assembled = new();
        await _store.AssembleAsync(7, 3, assembled, null, CancellationToken.None);

        Assert.Equal("Hello resumable world", Encoding.UTF8.GetString(assembled.ToArray()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AssembleAsync_FeedsHash()
    {
        await _store.WriteChunkAsync(7, 0, Bytes("abc"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(7, 1, Bytes("def"), Identity, CancellationToken.None);

        string actual = await AssembleAndHashAsync(_store, 7, 2);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes("abcdef"))), actual);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Delete_RemovesAllChunks()
    {
        await _store.WriteChunkAsync(9, 0, Bytes("a"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(9, 1, Bytes("b"), Identity, CancellationToken.None);

        _store.Delete(9);

        Assert.Equal(0, _store.GetContiguousChunkCount(9, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetContiguousChunkCount_WithDifferentIdentity_ReportsZeroButKeepsFiles()
    {
        await _store.WriteChunkAsync(5, 0, Bytes("old-a"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(5, 1, Bytes("old-b"), Identity, CancellationToken.None);

        int count = _store.GetContiguousChunkCount(5, OtherIdentity);

        // The read path reports nothing to resume but must not destroy another upload's state.
        Assert.Equal(0, count);
        Assert.Equal(2, _store.GetContiguousChunkCount(5, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteChunkAsync_WithDifferentIdentity_StartsFresh()
    {
        await _store.WriteChunkAsync(6, 0, Bytes("old-a"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(6, 1, Bytes("old-b"), Identity, CancellationToken.None);

        // A new output for the same task writes chunk 0 under a new identity.
        await _store.WriteChunkAsync(6, 0, Bytes("new-a"), OtherIdentity, CancellationToken.None);

        Assert.Equal(1, _store.GetContiguousChunkCount(6, OtherIdentity));

        string actual = await AssembleAndHashAsync(_store, 6, 1);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes("new-a"))), actual);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteChunkAsync_LegacyClientWithoutIdentity_DoesNotReset()
    {
        await _store.WriteChunkAsync(8, 0, Bytes("a"), null, CancellationToken.None);
        await _store.WriteChunkAsync(8, 1, Bytes("b"), null, CancellationToken.None);

        Assert.Equal(2, _store.GetContiguousChunkCount(8, null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SweepStaleUploads_RemovesOldDirectoriesAndKeepsFreshOnes()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("stale"), Identity, CancellationToken.None);
        await _store.WriteChunkAsync(2, 0, Bytes("fresh"), Identity, CancellationToken.None);

        Directory.SetLastWriteTimeUtc(
            _store.GetTaskDirectory(1),
            DateTime.UtcNow - TimeSpan.FromHours(48)
        );

        int removed = _store.SweepStaleUploads(TimeSpan.FromHours(24));

        Assert.Equal(1, removed);
        Assert.Equal(0, _store.GetContiguousChunkCount(1, Identity));
        Assert.Equal(1, _store.GetContiguousChunkCount(2, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteChunkAsync_ConcurrentWritersForSameChunk_DoNotCorruptIt()
    {
        byte[] content = Bytes("the only content");
        Task[] writers = Enumerable
            .Range(0, 8)
            .Select(_ => _store.WriteChunkAsync(3, 0, content, Identity, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(writers);

        Assert.Equal(1, _store.GetContiguousChunkCount(3, Identity));

        string actual = await AssembleAndHashAsync(_store, 3, 1);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), actual);
    }
}
