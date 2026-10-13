using System.IO.Compression;
using MangaIngestWithUpscaling.Api;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Api;

/// <summary>
/// The chapter-image endpoint is authenticated but still serves bytes out of a library archive, so a
/// crafted chapter must not be able to make it buffer an unbounded entry in server memory.
/// </summary>
public class ImageControllerTests : IAsyncDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly ApplicationDbContext _dbContext;
    private readonly string _tempDir;

    public ImageControllerTests()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;
        _tempDir = Path.Combine(Path.GetTempPath(), $"image_controller_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask DisposeAsync()
    {
        await _testDb.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // A stream left open by a test that did not execute the response can hold the file on
            // Windows; cleanup is best effort.
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChapterImage_RejectsAnEntryOverThePageCapWithoutBufferingIt()
    {
        const string relative = "chapter.cbz";
        WriteChapter(relative, ("page.jpg", new byte[64]));
        Chapter chapter = await SeedChapterAsync(relative);

        IActionResult result = await CreateController(maxPageBytes: 16)
            .GetChapterImage(chapter.Id, "page.jpg");

        // Rejected from the declared length before the body is read: the response is a status result,
        // not a stream, so nothing was materialised to reject it.
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
        Assert.IsNotType<FileStreamResult>(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChapterImage_EntryWhoseDeclaredLengthLies_StreamsThenAbortsAtTheCap()
    {
        const string relative = "lie.cbz";
        // The central-directory record claims 4 decompressed bytes while the entry's body is 64. A
        // stored entry's stream is bounded by its compressed size, so the header lies about the
        // decompressed length: the declared-length check passes and the controller streams the entry.
        // The running byte count in PageSizeLimitedStream is what stops it, so a "materialise
        // everything, then reject" body would have exposed the whole entry and failed this test.
        string liePath = Path.Combine(_tempDir, relative);
        using (ZipArchive zip = ZipFile.Open(liePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("page.jpg", CompressionLevel.NoCompression);
            using Stream stream = entry.Open();
            stream.Write(new byte[64]);
        }

        PatchCentralDirectoryUncompressedSize(liePath, 4);
        using (ZipArchive archive = ZipFile.OpenRead(liePath))
        {
            Assert.Equal(4, archive.GetEntry("page.jpg")!.Length);
        }

        Chapter chapter = await SeedChapterAsync(relative);

        IActionResult result = await CreateController(maxPageBytes: 16)
            .GetChapterImage(chapter.Id, "page.jpg");

        var file = Assert.IsType<FileStreamResult>(result);
        using var buffer = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            file.FileStream.CopyToAsync(buffer, TestContext.Current.CancellationToken)
        );
        file.FileStream.Dispose();

        // The cap aborted the copy before the entry's full body was exposed.
        Assert.True(buffer.Length <= 16);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChapterImage_StreamsAnEntryWithinTheCap()
    {
        const string relative = "chapter.cbz";
        WriteChapter(relative, ("page.jpg", new byte[] { 1, 2, 3, 4 }));
        Chapter chapter = await SeedChapterAsync(relative);

        IActionResult result = await CreateController(maxPageBytes: 1024)
            .GetChapterImage(chapter.Id, "page.jpg");

        var file = Assert.IsType<FileStreamResult>(result);
        using var buffer = new MemoryStream();
        await file.FileStream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        file.FileStream.Dispose();

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer.ToArray());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChapterImage_MissingEntry_ReturnsNotFound()
    {
        const string relative = "chapter.cbz";
        WriteChapter(relative, ("page.jpg", new byte[] { 1 }));
        Chapter chapter = await SeedChapterAsync(relative);

        IActionResult result = await CreateController(maxPageBytes: 1024)
            .GetChapterImage(chapter.Id, "nope.jpg");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PageSizeLimitedStream_AbortsOnceTheCapIsExceeded()
    {
        using var inner = new MemoryStream(new byte[64]);
        using var limited = new PageSizeLimitedStream(inner, limit: 16);

        byte[] buffer = new byte[64];
        Assert.Throws<InvalidDataException>(() => limited.Read(buffer, 0, buffer.Length));
    }

    private ImageController CreateController(long maxPageBytes)
    {
        var controller = new ImageController(_dbContext, NullLogger<ImageController>.Instance)
        {
            MaxPageBytes = maxPageBytes,
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    private async Task<Chapter> SeedChapterAsync(string relativePath)
    {
        var library = new Library
        {
            Name = "lib",
            NotUpscaledLibraryPath = _tempDir,
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled"),
        };
        var manga = new Manga { PrimaryTitle = "manga", Library = library };
        var chapter = new Chapter
        {
            Manga = manga,
            RelativePath = relativePath,
            FileName = relativePath,
        };

        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.Add(manga);
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return chapter;
    }

    private void WriteChapter(string relativePath, params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_tempDir, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream stream = entry.Open();
            stream.Write(data);
        }
    }

    /// <summary>
    /// Rewrites the uncompressed-size field (offset 24) of the first central-directory record, so the
    /// archive's declared entry size disagrees with what its deflate stream yields. The local header
    /// and the compressed size are left intact, so the entry still reads and decompresses.
    /// </summary>
    private static void PatchCentralDirectoryUncompressedSize(string path, uint declaredSize)
    {
        byte[] bytes = File.ReadAllBytes(path);
        for (int i = 0; i + 28 <= bytes.Length; i++)
        {
            // Central directory file header signature 'PK\x01\x02'.
            if (
                bytes[i] == 0x50
                && bytes[i + 1] == 0x4B
                && bytes[i + 2] == 0x01
                && bytes[i + 3] == 0x02
            )
            {
                BitConverter.GetBytes(declaredSize).CopyTo(bytes, i + 24);
                File.WriteAllBytes(path, bytes);
                return;
            }
        }

        throw new InvalidOperationException("No central-directory record found to patch.");
    }
}
