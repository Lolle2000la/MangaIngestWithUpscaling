using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// Covers the decompression caps that the local whole-CBZ path applies while reading a source
/// archive, mirroring the streamed path's per-page and per-task limits. The caps are driven through
/// the internal <see cref="OnnxUpscaleEngine.RewriteCbzAsync"/> seam with small values so a crafted
/// archive proves the bound without generating a half-gigabyte entry.
/// </summary>
public class OnnxUpscaleEngineArchiveLimitTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineArchiveLimitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_archive_limits_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private OnnxUpscaleEngine CreateEngine() =>
        new(
            Substitute.For<IOnnxSessionFactory>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir }),
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

    [Fact]
    [Trait("Category", "Unit")]
    public void CopyArchiveEntryWithLimits_RejectsAnEntryOverTheEntryCap()
    {
        using var source = new MemoryStream(new byte[64]);
        using var destination = new MemoryStream();

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            OnnxUpscaleEngine.CopyArchiveEntryWithLimits(
                source,
                destination,
                maxEntryBytes: 16,
                maxArchiveBytes: long.MaxValue,
                archiveBytesAlreadyRead: 0,
                entryName: "001.png"
            )
        );

        Assert.Contains("001.png", ex.Message);
        // The chunk that crossed the cap is not written, so the destination never exceeds it.
        Assert.True(destination.Length <= 16);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CopyArchiveEntryWithLimits_RejectsWhenTheArchiveTotalWouldBeExceeded()
    {
        using var source = new MemoryStream(new byte[64]);
        using var destination = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            OnnxUpscaleEngine.CopyArchiveEntryWithLimits(
                source,
                destination,
                maxEntryBytes: long.MaxValue,
                maxArchiveBytes: 32,
                archiveBytesAlreadyRead: 16,
                entryName: "ComicInfo.xml"
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CopyArchiveEntryWithLimits_CopiesWhenWithinBothCaps()
    {
        byte[] payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();

        long copied = OnnxUpscaleEngine.CopyArchiveEntryWithLimits(
            source,
            destination,
            maxEntryBytes: 100,
            maxArchiveBytes: 1000,
            archiveBytesAlreadyRead: 0,
            entryName: "001.png"
        );

        Assert.Equal(payload.Length, copied);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewriteCbzAsync_OversizedImageEntry_FailsTheChapterAndPublishesNothing()
    {
        string input = CreateArchive(("001.png", new byte[256]));
        string output = Path.Combine(_tempDir, "image-out.cbz");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateEngine()
                .RewriteCbzAsync(
                    input,
                    output,
                    scale: 2,
                    CompressionFormat.Webp,
                    quality: 80,
                    progress: null,
                    maxEntryBytes: 16,
                    maxArchiveBytes: long.MaxValue,
                    CancellationToken.None
                )
        );

        Assert.False(File.Exists(output), "an oversized entry must not publish a partial archive");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewriteCbzAsync_OversizedNonImageEntry_FailsTheChapter()
    {
        string input = CreateArchive(("ComicInfo.xml", new byte[256]));
        string output = Path.Combine(_tempDir, "meta-out.cbz");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateEngine()
                .RewriteCbzAsync(
                    input,
                    output,
                    scale: 2,
                    CompressionFormat.Webp,
                    quality: 80,
                    progress: null,
                    maxEntryBytes: 16,
                    maxArchiveBytes: long.MaxValue,
                    CancellationToken.None
                )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewriteCbzAsync_ArchiveTotalAcrossEntries_FailsTheChapter()
    {
        // Each entry fits the per-entry cap; together they exceed the archive budget.
        string input = CreateArchive(("a.xml", new byte[6]), ("b.xml", new byte[6]));
        string output = Path.Combine(_tempDir, "total-out.cbz");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateEngine()
                .RewriteCbzAsync(
                    input,
                    output,
                    scale: 2,
                    CompressionFormat.Webp,
                    quality: 80,
                    progress: null,
                    maxEntryBytes: 100,
                    maxArchiveBytes: 10,
                    CancellationToken.None
                )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewriteCbzAsync_SmallNonImageEntryWithinCaps_PassesThrough()
    {
        // No image entries, so no model is needed: the capped non-image pass-through is exercised
        // and must still copy an entry within the caps.
        string input = CreateArchive(("ComicInfo.xml", "<ComicInfo/>"u8.ToArray()));
        string output = Path.Combine(_tempDir, "passthrough-out.cbz");

        await CreateEngine()
            .RewriteCbzAsync(
                input,
                output,
                scale: 2,
                CompressionFormat.Webp,
                quality: 80,
                progress: null,
                maxEntryBytes: 1000,
                maxArchiveBytes: 1000,
                CancellationToken.None
            );

        using ZipArchive archive = ZipFile.OpenRead(output);
        Assert.NotNull(archive.GetEntry("ComicInfo.xml"));
    }

    private string CreateArchive(params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }
}
