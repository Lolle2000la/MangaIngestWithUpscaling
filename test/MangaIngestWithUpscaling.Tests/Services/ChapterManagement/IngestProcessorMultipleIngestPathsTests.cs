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
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.ChapterManagement;

public class IngestProcessorMultipleIngestPathsTests : IDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly string _tempRoot;

    public IngestProcessorMultipleIngestPathsTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "ingest_multi_path_test_" + Guid.NewGuid().ToString("N")[..8]
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WithMultipleIngestPaths_ProcessesChaptersFromEveryPath()
    {
        await using ApplicationDbContext db = _testDb.Context;

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

        string ingestPathA = Path.Combine(_tempRoot, "ingestA");
        string ingestPathB = Path.Combine(_tempRoot, "ingestB");
        var lib = new Library
        {
            Name = "MultiPathLib",
            IngestPaths =
            [
                new LibraryIngestPath { Path = ingestPathA, SortOrder = 0 },
                new LibraryIngestPath { Path = ingestPathB, SortOrder = 1 },
            ],
            NotUpscaledLibraryPath = Path.Combine(_tempRoot, "regular"),
            UpscaledLibraryPath = Path.Combine(_tempRoot, "upscaled"),
            UpscaleOnIngest = false,
            StripDetectionMode = StripDetectionMode.None,
        };
        Directory.CreateDirectory(ingestPathA);
        Directory.CreateDirectory(ingestPathB);
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        db.Libraries.Add(lib);

        var manga = new Manga { PrimaryTitle = "Multi Series", Library = lib };
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

        var chapterA = new FoundChapter(
            "Chapter 1.cbz",
            "Multi Series/Chapter 1.cbz",
            ChapterStorageType.Cbz,
            new ExtractedMetadata("Multi Series", "Chapter 1", "1")
        );
        var chapterB = new FoundChapter(
            "Chapter 2.cbz",
            "Multi Series/Chapter 2.cbz",
            ChapterStorageType.Cbz,
            new ExtractedMetadata("Multi Series", "Chapter 2", "2")
        );

        chapterRecognition
            .FindAllChaptersAt(ingestPathA, lib.FilterRules, Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter> { chapterA }.ToAsyncEnumerable());
        chapterRecognition
            .FindAllChaptersAt(ingestPathB, lib.FilterRules, Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter> { chapterB }.ToAsyncEnumerable());

        renaming
            .ApplyRenameRules(Arg.Any<FoundChapter>(), lib.RenameRules)
            .Returns(ci => (FoundChapter)ci[0]!);

        cbz.ConvertToCbz(Arg.Any<FoundChapter>(), Arg.Any<string>())
            .Returns(ci => (FoundChapter)ci[0]!);

        await ingest.ProcessAsync(lib, TestContext.Current.CancellationToken);

        chapterRecognition
            .Received(1)
            .FindAllChaptersAt(ingestPathA, lib.FilterRules, Arg.Any<CancellationToken>());
        chapterRecognition
            .Received(1)
            .FindAllChaptersAt(ingestPathB, lib.FilterRules, Arg.Any<CancellationToken>());
        cbz.Received(2).ConvertToCbz(Arg.Any<FoundChapter>(), Arg.Any<string>());

        List<Chapter> chapters = await db.Chapters.ToListAsync(
            TestContext.Current.CancellationToken
        );
        Assert.Equal(2, chapters.Count);
        Assert.Contains(chapters, c => c.FileName == "Chapter 1.cbz");
        Assert.Contains(chapters, c => c.FileName == "Chapter 2.cbz");
    }
}
