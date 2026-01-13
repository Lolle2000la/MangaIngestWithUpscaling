using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitProcessingServiceTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<SplitProcessingService> _logger;
    private readonly ITaskQueue _taskQueue;
    private readonly IFileSystem _fileSystem;
    private readonly SplitProcessingService _service;

    public SplitProcessingServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();

        _logger = Substitute.For<ILogger<SplitProcessingService>>();
        _taskQueue = Substitute.For<ITaskQueue>();
        _fileSystem = Substitute.For<IFileSystem>();

        _service = new SplitProcessingService(_dbContext, _logger, _taskQueue, _fileSystem);
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
    }

    [Fact]
    public async Task ProcessDetectionResultsAsync_OnlySavesResultsWithSplits()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/path",
            StripDetectionMode = StripDetectionMode.DetectOnly,
        };
        _dbContext.Libraries.Add(library);
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
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var results = new List<SplitDetectionResult>
        {
            // Image with splits - should be saved
            new()
            {
                ImagePath = "/test/page1.png",
                OriginalHeight = 1000,
                OriginalWidth = 500,
                Splits =
                [
                    new DetectedSplit { YOriginal = 500, Confidence = 0.9 },
                    new DetectedSplit { YOriginal = 750, Confidence = 0.85 },
                ],
                Count = 2,
            },
            // Image with no splits - should NOT be saved
            new()
            {
                ImagePath = "/test/page2.png",
                OriginalHeight = 800,
                OriginalWidth = 600,
                Splits = [],
                Count = 0,
            },
            // Another image with splits - should be saved
            new()
            {
                ImagePath = "/test/page3.png",
                OriginalHeight = 1200,
                OriginalWidth = 500,
                Splits = [new DetectedSplit { YOriginal = 600, Confidence = 0.95 }],
                Count = 1,
            },
        };

        // Act
        await _service.ProcessDetectionResultsAsync(
            chapter.Id,
            results,
            1,
            TestContext.Current.CancellationToken
        );

        // Assert
        var findings = await _dbContext
            .StripSplitFindings.Where(f => f.ChapterId == chapter.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Only 2 findings should be saved (page1 and page3 with splits)
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.PageFileName == "page1");
        Assert.Contains(findings, f => f.PageFileName == "page3");
        Assert.DoesNotContain(findings, f => f.PageFileName == "page2");
    }

    [Fact]
    public async Task ProcessDetectionResultsAsync_EnqueuesApplyTask_WhenSplitsExistAndModeIsDetectAndApply()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/path",
            StripDetectionMode = StripDetectionMode.DetectAndApply,
        };
        _dbContext.Libraries.Add(library);
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
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var results = new List<SplitDetectionResult>
        {
            new()
            {
                ImagePath = "/test/page1.png",
                OriginalHeight = 1000,
                OriginalWidth = 500,
                Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                Count = 1,
            },
        };

        // Act
        await _service.ProcessDetectionResultsAsync(
            chapter.Id,
            results,
            1,
            TestContext.Current.CancellationToken
        );

        // Assert
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<ApplySplitsTask>());

        var state = await _dbContext.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapter.Id,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(state);
        Assert.Equal(SplitProcessingStatus.Processing, state.Status);
    }

    [Fact]
    public async Task ProcessDetectionResultsAsync_DoesNotEnqueueApplyTask_WhenNoSplitsExist()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/path",
            StripDetectionMode = StripDetectionMode.DetectAndApply,
        };
        _dbContext.Libraries.Add(library);
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
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Results with no splits
        var results = new List<SplitDetectionResult>
        {
            new()
            {
                ImagePath = "/test/page1.png",
                OriginalHeight = 800,
                OriginalWidth = 600,
                Splits = [],
                Count = 0,
            },
        };

        // Act
        await _service.ProcessDetectionResultsAsync(
            chapter.Id,
            results,
            1,
            TestContext.Current.CancellationToken
        );

        // Assert
        // ApplySplitsTask should NOT be enqueued
        await _taskQueue.DidNotReceive().EnqueueAsync(Arg.Any<ApplySplitsTask>());

        // But UpscaleTask might be enqueued if upscaling is configured
        var state = await _dbContext.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapter.Id,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(state);
        Assert.Equal(SplitProcessingStatus.NoSplitsFound, state.Status);
    }

    [Fact]
    public async Task ProcessDetectionResultsAsync_EnqueuesUpscaleTask_WhenNoSplitsButUpscalingEnabled()
    {
        // Arrange
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/path",
            StripDetectionMode = StripDetectionMode.DetectAndApply,
            UpscaleOnIngest = true,
            UpscalerProfileId = profile.Id,
        };
        _dbContext.Libraries.Add(library);
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
            IsUpscaled = false,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Results with no splits
        var results = new List<SplitDetectionResult>
        {
            new()
            {
                ImagePath = "/test/page1.png",
                OriginalHeight = 800,
                OriginalWidth = 600,
                Splits = [],
                Count = 0,
            },
        };

        // Act
        await _service.ProcessDetectionResultsAsync(
            chapter.Id,
            results,
            1,
            TestContext.Current.CancellationToken
        );

        // Assert
        // ApplySplitsTask should NOT be enqueued
        await _taskQueue.DidNotReceive().EnqueueAsync(Arg.Any<ApplySplitsTask>());

        // UpscaleTask should be enqueued since upscaling is enabled
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<UpscaleTask>());
    }
}
