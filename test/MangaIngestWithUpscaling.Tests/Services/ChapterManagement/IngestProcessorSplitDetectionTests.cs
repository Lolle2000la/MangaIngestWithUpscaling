using System.IO.Compression;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.CbzConversion;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.ChapterManagement;

public class IngestProcessorSplitDetectionTests : IDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly string _tempRoot;

    public IngestProcessorSplitDetectionTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "ingest_split_test_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _testDb?.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, true);
            }
            catch { }
        }
    }

    private ApplicationDbContext CreateDb() => _testDb.Context;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WithStripDetectionModeDetectAndApply_QueuesSplitDetectionAndDefersUpscale_WhenPlausible()
    {
        await using ApplicationDbContext db = CreateDb();

        var chapterRecognition = Substitute.For<IChapterInIngestRecognitionService>();
        var renaming = Substitute.For<ILibraryRenamingService>();
        var cbz = Substitute.For<ICbzConverter>();
        var logger = Substitute.For<ILogger<IngestProcessor>>();
        var metadata = Substitute.For<IMetadataHandlingService>();
        var fs = Substitute.For<IFileSystem>();
        var changedNotifier = Substitute.For<IChapterChangedNotifier>();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var mergeCoordinator = Substitute.For<IChapterMergeCoordinator>();
        var imageFilter = Substitute.For<IImageFilterService>();
        var chapterProcessingService = Substitute.For<IChapterProcessingService>();
        var splitCoordinator = Substitute.For<ISplitProcessingCoordinator>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddScoped<IQueueCleanup, QueueCleanup>();
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var taskQueue = new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>());
        var processor = new UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            Options.Create(new UpscalerConfig { RemoteOnly = true }),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>(),
            new PreprocessedInputCache()
        );

        var ingest = new IngestProcessor(
            db,
            chapterRecognition,
            renaming,
            cbz,
            logger,
            taskQueue,
            metadata,
            fs,
            changedNotifier,
            chapterPartMerger,
            mergeCoordinator,
            processor,
            imageFilter,
            chapterProcessingService,
            splitCoordinator,
            Substitute.For<IStringLocalizer<IngestProcessor>>()
        );

        var lib = new Library
        {
            Name = "WebtoonLib",
            IngestPath = Path.Combine(_tempRoot, "ingest"),
            NotUpscaledLibraryPath = Path.Combine(_tempRoot, "regular"),
            UpscaledLibraryPath = Path.Combine(_tempRoot, "upscaled"),
            UpscaleOnIngest = true,
            StripDetectionMode = StripDetectionMode.DetectAndApply,
            UpscalerProfile = new UpscalerProfile
            {
                Name = "P",
                ScalingFactor = ScaleFactor.TwoX,
                CompressionFormat = CompressionFormat.Png,
                Quality = 80,
            },
        };
        Directory.CreateDirectory(lib.IngestPath);
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath);
        db.Libraries.Add(lib);

        var manga = new Manga { PrimaryTitle = "Webtoon Series", Library = lib };
        db.MangaSeries.Add(manga);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        chapterProcessingService
            .DetectUpscaledFileAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult((false, (UpscalerProfileJsonDto?)null)));

        chapterProcessingService
            .GetOrCreateMangaSeriesAsync(
                Arg.Any<Library>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(manga));

        var meta = new ExtractedMetadata("Webtoon Series", "Chapter 1", "1");
        var foundChapter = new FoundChapter(
            "Chapter 1.cbz",
            "Webtoon Series/Chapter 1.cbz",
            ChapterStorageType.Cbz,
            meta
        );

        chapterRecognition
            .FindAllChaptersAt(lib.IngestPath, lib.FilterRules, Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter> { foundChapter }.ToAsyncEnumerable());

        renaming
            .ApplyRenameRules(Arg.Any<FoundChapter>(), lib.RenameRules)
            .Returns(ci => (FoundChapter)ci[0]!);

        cbz.ConvertToCbz(Arg.Any<FoundChapter>(), lib.IngestPath)
            .Returns(ci => (FoundChapter)ci[0]!);

        // Mock split coordinator: returns true (split detection enqueued)
        splitCoordinator
            .EnqueueDetectionIfPlausibleAsync(
                Arg.Any<int>(),
                Arg.Any<ApplicationDbContext>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(true));

        // Act
        await ingest.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // Assert
        // Split detection should have been called
        await splitCoordinator
            .Received(1)
            .EnqueueDetectionIfPlausibleAsync(
                Arg.Any<int>(),
                Arg.Any<ApplicationDbContext>(),
                Arg.Any<CancellationToken>()
            );

        // Upscale task should NOT have been enqueued on ingest (deferred for split detection)
        IReadOnlyList<PersistedTask> snapshot = taskQueue.GetUpscaleSnapshot();
        Assert.DoesNotContain(snapshot, t => t.Data is UpscaleTask);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WithStripDetectionModeDetectAndApply_QueuesUpscaleImmediately_WhenNotPlausible()
    {
        await using ApplicationDbContext db = CreateDb();

        var chapterRecognition = Substitute.For<IChapterInIngestRecognitionService>();
        var renaming = Substitute.For<ILibraryRenamingService>();
        var cbz = Substitute.For<ICbzConverter>();
        var logger = Substitute.For<ILogger<IngestProcessor>>();
        var metadata = Substitute.For<IMetadataHandlingService>();
        var fs = Substitute.For<IFileSystem>();
        var changedNotifier = Substitute.For<IChapterChangedNotifier>();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var mergeCoordinator = Substitute.For<IChapterMergeCoordinator>();
        var imageFilter = Substitute.For<IImageFilterService>();
        var chapterProcessingService = Substitute.For<IChapterProcessingService>();
        var splitCoordinator = Substitute.For<ISplitProcessingCoordinator>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddScoped<IQueueCleanup, QueueCleanup>();
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var taskQueue = new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>());
        var processor = new UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            Options.Create(new UpscalerConfig { RemoteOnly = true }),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>(),
            new PreprocessedInputCache()
        );

        var ingest = new IngestProcessor(
            db,
            chapterRecognition,
            renaming,
            cbz,
            logger,
            taskQueue,
            metadata,
            fs,
            changedNotifier,
            chapterPartMerger,
            mergeCoordinator,
            processor,
            imageFilter,
            chapterProcessingService,
            splitCoordinator,
            Substitute.For<IStringLocalizer<IngestProcessor>>()
        );

        var lib = new Library
        {
            Name = "RegularLib",
            IngestPath = Path.Combine(_tempRoot, "ingest2"),
            NotUpscaledLibraryPath = Path.Combine(_tempRoot, "regular2"),
            UpscaledLibraryPath = Path.Combine(_tempRoot, "upscaled2"),
            UpscaleOnIngest = true,
            StripDetectionMode = StripDetectionMode.DetectAndApply,
            UpscalerProfile = new UpscalerProfile
            {
                Name = "P",
                ScalingFactor = ScaleFactor.TwoX,
                CompressionFormat = CompressionFormat.Png,
                Quality = 80,
            },
        };
        Directory.CreateDirectory(lib.IngestPath);
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath);
        db.Libraries.Add(lib);

        var manga = new Manga { PrimaryTitle = "Regular Manga", Library = lib };
        db.MangaSeries.Add(manga);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        chapterProcessingService
            .DetectUpscaledFileAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult((false, (UpscalerProfileJsonDto?)null)));

        chapterProcessingService
            .GetOrCreateMangaSeriesAsync(
                Arg.Any<Library>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(manga));

        var meta = new ExtractedMetadata("Regular Manga", "Chapter 1", "1");
        var foundChapter = new FoundChapter(
            "Chapter 1.cbz",
            "Regular Manga/Chapter 1.cbz",
            ChapterStorageType.Cbz,
            meta
        );

        chapterRecognition
            .FindAllChaptersAt(lib.IngestPath, lib.FilterRules, Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter> { foundChapter }.ToAsyncEnumerable());

        renaming
            .ApplyRenameRules(Arg.Any<FoundChapter>(), lib.RenameRules)
            .Returns(ci => (FoundChapter)ci[0]!);

        cbz.ConvertToCbz(Arg.Any<FoundChapter>(), lib.IngestPath)
            .Returns(ci => (FoundChapter)ci[0]!);

        // Mock split coordinator: returns false (no splits found / not plausible)
        splitCoordinator
            .EnqueueDetectionIfPlausibleAsync(
                Arg.Any<int>(),
                Arg.Any<ApplicationDbContext>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(false));

        // Act
        await ingest.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // Assert
        // Split detection should have been checked
        await splitCoordinator
            .Received(1)
            .EnqueueDetectionIfPlausibleAsync(
                Arg.Any<int>(),
                Arg.Any<ApplicationDbContext>(),
                Arg.Any<CancellationToken>()
            );

        // Upscale task should have been enqueued immediately
        IReadOnlyList<PersistedTask> snapshot = taskQueue.GetUpscaleSnapshot();
        Assert.Contains(snapshot, t => t.Data is UpscaleTask);
    }
}
