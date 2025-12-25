using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitApplicationServiceTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISplitProcessingCoordinator _coordinator;
    private readonly ISplitApplier _splitApplier;
    private readonly ILogger<SplitApplicationService> _logger;
    private readonly SplitApplicationService _service;
    private readonly string _tempDir;

    public SplitApplicationServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();

        _coordinator = Substitute.For<ISplitProcessingCoordinator>();
        _splitApplier = Substitute.For<ISplitApplier>();
        _logger = Substitute.For<ILogger<SplitApplicationService>>();

        _service = new SplitApplicationService(_dbContext, _coordinator, _splitApplier, _logger);

        _tempDir = Path.Combine(Path.GetTempPath(), $"split_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task ApplySplitsAsync_WhenChapterIsUpscaled_DeletesUpscaledVersion()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled"),
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath);

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = true,
            UpscalerProfileId = profile.Id,
            UpscalerProfile = profile,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ with a test image
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "page1.png");

        // Create upscaled CBZ
        var upscaledCbzPath = chapter.UpscaledFullPath!;
        CreateTestCbz(upscaledCbzPath, "page1.png");

        // Create split finding
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page1",
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        _dbContext.StripSplitFindings.Add(finding);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier to create dummy split files
        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                var part2 = Path.Combine(outputDir, "page1_part2.png");
                File.WriteAllText(part1, "dummy");
                File.WriteAllText(part2, "dummy");
                return new List<string> { part1, part2 };
            });

        // Act
        await _service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert
        // Reload chapter from database
        await _dbContext.Entry(chapter).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.False(chapter.IsUpscaled, "Chapter should be marked as not upscaled");
        Assert.Null(chapter.UpscalerProfileId);
        Assert.False(File.Exists(upscaledCbzPath), "Upscaled CBZ should be deleted");

        // Verify coordinator was notified
        await _coordinator
            .Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySplitsAsync_WhenChapterIsNotUpscaled_DoesNotAttemptToDeleteUpscaled()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original"),
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = false,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "page1.png");

        // Create split finding
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page1",
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        _dbContext.StripSplitFindings.Add(finding);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier
        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                var part2 = Path.Combine(outputDir, "page1_part2.png");
                File.WriteAllText(part1, "dummy");
                File.WriteAllText(part2, "dummy");
                return new List<string> { part1, part2 };
            });

        // Act
        await _service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert
        await _dbContext.Entry(chapter).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(chapter.IsUpscaled);

        await _coordinator
            .Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    private void CreateTestCbz(string path, string imageName)
    {
        var tempExtractDir = Path.Combine(_tempDir, $"extract_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            // Create a dummy image file
            var imagePath = Path.Combine(tempExtractDir, imageName);
            File.WriteAllText(imagePath, "dummy image content");

            // Create CBZ (which is just a ZIP file)
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            ZipFile.CreateFromDirectory(tempExtractDir, path);
        }
        finally
        {
            if (Directory.Exists(tempExtractDir))
            {
                Directory.Delete(tempExtractDir, true);
            }
        }
    }
}
