using System.Text;
using MangaIngestWithUpscaling.Services.Uploads;

namespace MangaIngestWithUpscaling.Tests.Services.Uploads;

public class ResumableUploadStoreTests : IDisposable
{
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetContiguousChunkCount_StopsAtFirstGap()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("a"), CancellationToken.None);
        await _store.WriteChunkAsync(1, 1, Bytes("b"), CancellationToken.None);
        await _store.WriteChunkAsync(1, 3, Bytes("d"), CancellationToken.None);

        Assert.Equal(2, _store.GetContiguousChunkCount(1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetContiguousChunkCount_IsolatedPerTask()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("a"), CancellationToken.None);
        await _store.WriteChunkAsync(12, 0, Bytes("x"), CancellationToken.None);
        await _store.WriteChunkAsync(12, 1, Bytes("y"), CancellationToken.None);

        Assert.Equal(1, _store.GetContiguousChunkCount(1));
        Assert.Equal(2, _store.GetContiguousChunkCount(12));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetContiguousChunkCount_NoChunks_ReturnsZero()
    {
        Assert.Equal(0, _store.GetContiguousChunkCount(42));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteChunkAsync_OverwritesExistingChunk()
    {
        await _store.WriteChunkAsync(1, 0, Bytes("old"), CancellationToken.None);
        await _store.WriteChunkAsync(1, 0, Bytes("new"), CancellationToken.None);

        Assert.Equal(1, _store.GetContiguousChunkCount(1));

        await using MemoryStream assembled = new();
        await _store.AssembleAsync(1, 1, assembled, CancellationToken.None);
        Assert.Equal("new", Encoding.UTF8.GetString(assembled.ToArray()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AssembleAsync_ConcatenatesChunksInOrder()
    {
        await _store.WriteChunkAsync(7, 0, Bytes("Hello "), CancellationToken.None);
        await _store.WriteChunkAsync(7, 1, Bytes("resumable "), CancellationToken.None);
        await _store.WriteChunkAsync(7, 2, Bytes("world"), CancellationToken.None);

        await using MemoryStream assembled = new();
        await _store.AssembleAsync(7, 3, assembled, CancellationToken.None);

        Assert.Equal("Hello resumable world", Encoding.UTF8.GetString(assembled.ToArray()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Delete_RemovesAllChunks()
    {
        await _store.WriteChunkAsync(9, 0, Bytes("a"), CancellationToken.None);
        await _store.WriteChunkAsync(9, 1, Bytes("b"), CancellationToken.None);

        _store.Delete(9);

        Assert.Equal(0, _store.GetContiguousChunkCount(9));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteChunkAsync_ConcurrentWritersForSameChunk_DoNotCorruptIt()
    {
        byte[] content = Bytes("the only content");
        Task[] writers = Enumerable
            .Range(0, 8)
            .Select(_ => _store.WriteChunkAsync(3, 0, content, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(writers);

        Assert.Equal(1, _store.GetContiguousChunkCount(3));
        await using MemoryStream assembled = new();
        await _store.AssembleAsync(3, 1, assembled, CancellationToken.None);
        Assert.Equal("the only content", Encoding.UTF8.GetString(assembled.ToArray()));
    }
}
