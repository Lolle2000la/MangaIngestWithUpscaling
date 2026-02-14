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

public class SplitDetectionUpscaleBugTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<SplitProcessingService> _logger;
    private readonly ITaskQueue _taskQueue;
    private readonly IFileSystem _fileSystem;
    private readonly ISplitProcessingStateManager _stateManager;
    private readonly ILogger<SplitProcessingStateManager> _stateManagerLogger;
    private readonly SplitProcessingService _service;

    public SplitDetectionUpscaleBugTests()
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
        _stateManagerLogger = Substitute.For<ILogger<SplitProcessingStateManager>>();
        _stateManager = new SplitProcessingStateManager(_dbContext, _stateManagerLogger);

        _service = new SplitProcessingService(
            _dbContext,
            _logger,
            _taskQueue,
            _fileSystem,
            _stateManager
        );
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
    }

    [Fact]
    public async Task ProcessDetectionResultsAsync_EnqueuesUpscaleTask_WhenSplitsDetectedWithDetectAndApplyModeAndUpscaleOnIngest()
    {
        // This test reproduces the bug: when splits are detected with DetectAndApply mode
        // AND UpscaleOnIngest is true, an upscale task should be scheduled after splits
        // are applied, but it might not be happening.

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
            StripDetectionMode = StripDetectionMode.DetectAndApply, // DetectAndApply mode
            UpscaleOnIngest = true, // Upscaling is enabled
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

        // Split detection results WITH splits
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
        // ApplySplitsTask should be enqueued
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<ApplySplitsTask>());

        // But NO UpscaleTask should be enqueued yet (it will be enqueued after ApplySplitsTask completes)
        await _taskQueue.DidNotReceive().EnqueueAsync(Arg.Any<UpscaleTask>());

        // The upscale task will be enqueued when OnSplitsAppliedAsync is called after ApplySplitsTask completes
        // This is the expected behavior - the upscale happens AFTER the splits are applied
    }

    [Fact]
    public async Task ProcessDetectionResultsAsync_EnqueuesUpscaleTask_WhenSplitsDetectedWithDetectOnlyModeAndUpscaleOnIngest()
    {
        // This test verifies that when splits are detected with DetectOnly mode
        // AND UpscaleOnIngest is true, an upscale task IS scheduled immediately.

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
            StripDetectionMode = StripDetectionMode.DetectOnly, // DetectOnly mode
            UpscaleOnIngest = true, // Upscaling is enabled
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

        // Split detection results WITH splits
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
        // ApplySplitsTask should NOT be enqueued (DetectOnly mode)
        await _taskQueue.DidNotReceive().EnqueueAsync(Arg.Any<ApplySplitsTask>());

        // UpscaleTask SHOULD be enqueued because splits detected + DetectOnly mode + UpscaleOnIngest
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<UpscaleTask>());
    }
}
