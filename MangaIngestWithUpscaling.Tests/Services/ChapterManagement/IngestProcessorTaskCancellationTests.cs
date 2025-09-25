using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.CbzConversion;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Tests.Services.ChapterManagement;

public class IngestProcessorTaskCancellationTests : IDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public IngestProcessorTaskCancellationTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
    }

    public void Dispose()
    {
        _testDb?.Dispose();
    }

    private ApplicationDbContext CreateDb()
    {
        return _testDb.Context;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_MergeCancelsOriginalUpscaleTasks_QueuesMergedTaskOnly()
    {
        await using ApplicationDbContext db = CreateDb();

        // Arrange basic services (mostly substitutes) and a real TaskQueue+Processor
        var chapterRecognition = Substitute.For<IChapterInIngestRecognitionService>();
        var renaming = Substitute.For<ILibraryRenamingService>();
        var cbz = Substitute.For<ICbzConverter>();
        var logger = Substitute.For<ILogger<IngestProcessor>>();
        var metadata = Substitute.For<IMetadataHandlingService>();
        var fs = Substitute.For<IFileSystem>();
        var changedNotifier = Substitute.For<IChapterChangedNotifier>();
        var upscalerJson = Substitute.For<IUpscalerJsonHandlingService>();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var mergeCoordinator = Substitute.For<IChapterMergeCoordinator>();
        var imageFilter = Substitute.For<IImageFilterService>();

        // Real queue and processor
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddScoped<IQueueCleanup, QueueCleanup>();
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var taskQueue = new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>());
        IOptions<UpscalerConfig> upscalerOptions = Options.Create(new UpscalerConfig { RemoteOnly = true });
        var processorLogger = Substitute.For<ILogger<UpscaleTaskProcessor>>();
        var processor = new UpscaleTaskProcessor(taskQueue, scopeFactory, upscalerOptions, processorLogger);

        // SUT
        var chapterProcessingService = Substitute.For<ChapterProcessingService>();
        var ingest = new IngestProcessor(db, chapterRecognition, renaming, cbz, logger, taskQueue, metadata, fs,
            changedNotifier, chapterPartMerger, mergeCoordinator, processor, imageFilter, chapterProcessingService);

        // Library and series
        string tempRoot = Path.Combine(Path.GetTempPath(),
            "ingest_merge_cancel_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tempRoot);
        var lib = new Library
        {
            Name = "Lib",
            IngestPath = tempRoot,
            NotUpscaledLibraryPath = Path.Combine(tempRoot, "regular"),
            UpscaledLibraryPath = Path.Combine(tempRoot, "upscaled"),
            UpscaleOnIngest = true,
            MergeChapterParts = true,
            UpscalerProfile = new UpscalerProfile
            {
                Name = "P",
                ScalingFactor = ScaleFactor.TwoX,
                CompressionFormat = CompressionFormat.Png,
                Quality = 80
            }
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);
        db.Libraries.Add(lib);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Simulate recognition of two parts that will be merged into Chapter 1.cbz
        var meta1 = new ExtractedMetadata("Series", "Chapter 1.1", "1.1");
        var meta2 = new ExtractedMetadata("Series", "Chapter 1.2", "1.2");
        var in1 = new FoundChapter("Chapter 1.1.cbz", "Series/Chapter 1.1.cbz", ChapterStorageType.Cbz, meta1);
        var in2 = new FoundChapter("Chapter 1.2.cbz", "Series/Chapter 1.2.cbz", ChapterStorageType.Cbz, meta2);

        // Recognition returns two chapters
        chapterRecognition.FindAllChaptersAt(lib.IngestPath, lib.FilterRules, Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter> { in1, in2 }.ToAsyncEnumerable());

        // Renaming keeps names (no changes)
        renaming.ApplyRenameRules(Arg.Any<FoundChapter>(), lib.RenameRules)
            .Returns(ci => (FoundChapter)ci[0]!);

        // cbzConverter returns the same relative path (pretend already CBZ)
        cbz.ConvertToCbz(Arg.Any<FoundChapter>(), lib.IngestPath)
            .Returns(ci => (FoundChapter)ci[0]!);

        // The merger will produce a merge result that merges the two parts
        string seriesDir = Path.Combine(lib.NotUpscaledLibraryPath, "Series");
        Directory.CreateDirectory(seriesDir);
        var mergedFound = new FoundChapter("Chapter 1.cbz", "Series/Chapter 1.cbz", ChapterStorageType.Cbz,
            new ExtractedMetadata("Series", "Chapter 1", "1"));
        // Create a dummy merged file at the expected location so DB step doesn’t throw
        await using (ZipArchive zip = await ZipFile.OpenAsync(Path.Combine(seriesDir, mergedFound.FileName),
                         ZipArchiveMode.Create, TestContext.Current.CancellationToken)) { }

        chapterPartMerger.ProcessChapterMergingAsync(
                Arg.Any<List<FoundChapter>>(),
                Arg.Is(lib.IngestPath),
                Arg.Is(seriesDir),
                Arg.Is("Series"),
                Arg.Any<HashSet<string>>(),
                Arg.Any<Func<FoundChapter, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var mi = new MergeInfo(
                    mergedFound,
                    new List<OriginalChapterPart>
                    {
                        new() { FileName = "Chapter 1.1.cbz", PageNames = new List<string> { "001.jpg" } },
                        new() { FileName = "Chapter 1.2.cbz", PageNames = new List<string> { "002.jpg" } }
                    },
                    "1");

                return new ChapterMergeResult(new List<FoundChapter> { mergedFound }, new List<MergeInfo> { mi });
            });

        // Create two original chapter entities with pre-existing UpscaleTasks that should be removed
        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        db.MangaSeries.Add(manga);
        var ch11 = new Chapter
        {
            FileName = "Chapter 1.1.cbz", RelativePath = Path.Combine("Series", "Chapter 1.1.cbz"), Manga = manga
        };
        var ch12 = new Chapter
        {
            FileName = "Chapter 1.2.cbz", RelativePath = Path.Combine("Series", "Chapter 1.2.cbz"), Manga = manga
        };
        db.Chapters.AddRange(ch11, ch12);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Queue existing UpscaleTasks for originals
        await taskQueue.EnqueueAsync(new UpscaleTask(ch11));
        await taskQueue.EnqueueAsync(new UpscaleTask(ch12));

        // Act: process ingest which will merge the two parts
        await ingest.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // Assert: original UpscaleTasks should be gone, only merged chapter task should exist
        IReadOnlyList<PersistedTask> snapshot = taskQueue.GetUpscaleSnapshot();

        // No task for original parts
        Assert.DoesNotContain(snapshot,
            t => t.Data is UpscaleTask ut && (ut.ChapterId == ch11.Id || ut.ChapterId == ch12.Id));

        // Exactly one task for the merged chapter
        Assert.Contains(snapshot, t => t.Data is UpscaleTask ut && ut.ChapterId != ch11.Id && ut.ChapterId != ch12.Id);
    }
}