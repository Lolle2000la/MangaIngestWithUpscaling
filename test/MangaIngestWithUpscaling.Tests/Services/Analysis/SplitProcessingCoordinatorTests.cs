using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitProcessingCoordinatorTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITaskQueue _taskQueue;
    private readonly IChapterChangedNotifier _chapterChangedNotifier;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<SplitProcessingCoordinator> _logger;
    private readonly ISplitProcessingStateManager _stateManager;
    private readonly ILogger<SplitProcessingStateManager> _stateManagerLogger;
    private readonly SplitProcessingCoordinator _coordinator;

    public SplitProcessingCoordinatorTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();

        _taskQueue = Substitute.For<ITaskQueue>();
        _chapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        _fileSystem = Substitute.For<IFileSystem>();
        _logger = Substitute.For<ILogger<SplitProcessingCoordinator>>();
        _stateManagerLogger = Substitute.For<ILogger<SplitProcessingStateManager>>();
        _stateManager = new SplitProcessingStateManager(_dbContext, _stateManagerLogger);

        _coordinator = new SplitProcessingCoordinator(
            _dbContext,
            _taskQueue,
            _chapterChangedNotifier,
            _fileSystem,
            _logger,
            _stateManager
        );
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
    }

    private async Task<Chapter> CreateChapterAsync()
    {
        var library = new Library { Name = "Test Lib" };
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
            FileName = "ch1.zip",
            MangaId = manga.Id,
            Manga = manga,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return chapter;
    }

    [Fact]
    public async Task ShouldProcessAsync_ReturnsTrue_WhenNoStateExists()
    {
        // Arrange
        var chapterId = 1;

        // Act
        var result = await _coordinator.ShouldProcessAsync(
            chapterId,
            StripDetectionMode.DetectOnly,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ShouldProcessAsync_ReturnsTrue_WhenVersionIsOld()
    {
        // Arrange
        var chapter = await CreateChapterAsync();
        _dbContext.ChapterSplitProcessingStates.Add(
            new ChapterSplitProcessingState
            {
                ChapterId = chapter.Id,
                LastProcessedDetectorVersion = SplitDetectionService.CURRENT_DETECTOR_VERSION - 1,
                ModifiedAt = DateTime.UtcNow,
            }
        );
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _coordinator.ShouldProcessAsync(
            chapter.Id,
            StripDetectionMode.DetectOnly,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ShouldProcessAsync_ReturnsFalse_WhenVersionIsCurrent()
    {
        // Arrange
        var chapter = await CreateChapterAsync();
        _dbContext.ChapterSplitProcessingStates.Add(
            new ChapterSplitProcessingState
            {
                ChapterId = chapter.Id,
                LastProcessedDetectorVersion = SplitDetectionService.CURRENT_DETECTOR_VERSION,
                ModifiedAt = DateTime.UtcNow,
            }
        );
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _coordinator.ShouldProcessAsync(
            chapter.Id,
            StripDetectionMode.DetectOnly,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task EnqueueDetectionAsync_EnqueuesTask()
    {
        // Arrange
        var chapterId = 1;

        // Act
        await _coordinator.EnqueueDetectionAsync(chapterId, TestContext.Current.CancellationToken);

        // Assert
        await _taskQueue
            .Received(1)
            .EnqueueAsync(
                Arg.Is<DetectSplitCandidatesTask>(t =>
                    t.ChapterId == chapterId
                    && t.DetectorVersion == SplitDetectionService.CURRENT_DETECTOR_VERSION
                )
            );
    }
}
