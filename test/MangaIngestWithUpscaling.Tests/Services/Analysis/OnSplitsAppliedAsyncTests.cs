using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class OnSplitsAppliedAsyncTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITaskQueue _taskQueue;
    private readonly IChapterChangedNotifier _chapterChangedNotifier;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<SplitProcessingCoordinator> _logger;
    private readonly ISplitProcessingStateManager _stateManager;
    private readonly ILogger<SplitProcessingStateManager> _stateManagerLogger;
    private readonly SplitProcessingCoordinator _coordinator;

    public OnSplitsAppliedAsyncTests()
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

    [Fact]
    public async Task OnSplitsAppliedAsync_WithMangaInheritingFromLibrary_SchedulesUpscaleTask()
    {
        // This test reproduces the bug where manga with no specific preference
        // (inheriting from library) don't get upscale tasks scheduled

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
            UpscaledLibraryPath = "/test/upscaled",
            UpscaleOnIngest = true,
            UpscalerProfileId = profile.Id, // Library has a profile
            UpscalerProfile = profile,
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
            // NO UpscalerProfilePreference - inherits from library
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

        // Act
        await _coordinator.OnSplitsAppliedAsync(
            chapter.Id,
            1,
            TestContext.Current.CancellationToken
        );

        // Assert
        // UpscaleTask should be enqueued because UpscaleOnIngest is true
        // and the manga inherits the profile from the library
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<UpscaleTask>());
    }

    [Fact]
    public async Task OnSplitsAppliedAsync_WithMangaWithSpecificPreference_SchedulesUpscaleTask()
    {
        // This test verifies that manga with a specific preference also work correctly

        // Arrange
        var libraryProfile = new UpscalerProfile
        {
            Name = "Library Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(libraryProfile);

        var mangaProfile = new UpscalerProfile
        {
            Name = "Manga Profile",
            ScalingFactor = ScaleFactor.FourX,
            CompressionFormat = CompressionFormat.Avif,
            Quality = 85,
        };
        _dbContext.UpscalerProfiles.Add(mangaProfile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/path",
            UpscaledLibraryPath = "/test/upscaled",
            UpscaleOnIngest = true,
            UpscalerProfileId = libraryProfile.Id,
            UpscalerProfile = libraryProfile,
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
            UpscalerProfilePreferenceId = mangaProfile.Id,
            UpscalerProfilePreference = mangaProfile, // Has specific preference
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

        // Act
        await _coordinator.OnSplitsAppliedAsync(
            chapter.Id,
            1,
            TestContext.Current.CancellationToken
        );

        // Assert
        // UpscaleTask should be enqueued with the manga's specific profile
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<UpscaleTask>());
    }
}
