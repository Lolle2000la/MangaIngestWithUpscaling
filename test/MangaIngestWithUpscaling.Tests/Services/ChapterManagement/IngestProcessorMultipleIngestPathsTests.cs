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

    private sealed record IngestSetup(
        IngestProcessor Processor,
        IChapterInIngestRecognitionService ChapterRecognition,
        ILibraryRenamingService Renaming,
        ICbzConverter Cbz,
        IChapterProcessingService ChapterProcessing,
        IChapterPartMerger ChapterPartMerger,
        IFileSystem FileSystem
    );

    private static IngestSetup BuildIngestProcessor(ApplicationDbContext db)
    {
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

        return new IngestSetup(
            ingest,
            chapterRecognition,
            renaming,
            cbz,
            chapterProcessingService,
            chapterPartMerger,
            fs
        );
    }

    private async Task<(Library Library, Manga Manga)> CreateLibraryAsync(
        ApplicationDbContext db,
        string seriesTitle,
        params string[] ingestPaths
    )
    {
        var library = new Library
        {
            Name = seriesTitle,
            IngestPaths = ingestPaths
                .Select((p, i) => new LibraryIngestPath { Path = p, SortOrder = i })
                .ToList(),
            NotUpscaledLibraryPath = Path.Combine(_tempRoot, seriesTitle + "_regular"),
            UpscaledLibraryPath = Path.Combine(_tempRoot, seriesTitle + "_upscaled"),
            UpscaleOnIngest = false,
            StripDetectionMode = StripDetectionMode.None,
        };
        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        db.Libraries.Add(library);

        var manga = new Manga { PrimaryTitle = seriesTitle, Library = library };
        db.MangaSeries.Add(manga);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (library, manga);
    }

    private static void StubCommonDependencies(IngestSetup setup, Library library, Manga manga)
    {
        setup
            .ChapterProcessing.DetectUpscaledFileAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult((false, (UpscalerProfileJsonDto?)null)));

        setup
            .ChapterProcessing.GetOrCreateMangaSeriesAsync(
                Arg.Any<Library>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(manga));

        setup
            .Renaming.ApplyRenameRules(Arg.Any<FoundChapter>(), library.RenameRules)
            .Returns(ci => (FoundChapter)ci[0]!);

        setup
            .Cbz.ConvertToCbz(Arg.Any<FoundChapter>(), Arg.Any<string>())
            .Returns(ci => (FoundChapter)ci[0]!);
    }

    private static FoundChapter Chapter(
        string series,
        string fileName,
        string title,
        string number
    ) =>
        new(
            fileName,
            Path.Combine(series, fileName),
            ChapterStorageType.Cbz,
            new ExtractedMetadata(series, title, number)
        );

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WithMultipleIngestPaths_ProcessesChaptersFromEveryPath()
    {
        await using ApplicationDbContext db = _testDb.Context;
        IngestSetup setup = BuildIngestProcessor(db);

        string ingestPathA = Path.Combine(_tempRoot, "ingestA");
        string ingestPathB = Path.Combine(_tempRoot, "ingestB");
        Directory.CreateDirectory(ingestPathA);
        Directory.CreateDirectory(ingestPathB);

        (Library lib, Manga manga) = await CreateLibraryAsync(
            db,
            "Multi Series",
            ingestPathA,
            ingestPathB
        );
        StubCommonDependencies(setup, lib, manga);

        FoundChapter chapterA = Chapter("Multi Series", "Chapter 1.cbz", "Chapter 1", "1");
        FoundChapter chapterB = Chapter("Multi Series", "Chapter 2.cbz", "Chapter 2", "2");

        setup
            .ChapterRecognition.FindAllChaptersAt(
                ingestPathA,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { chapterA }.ToAsyncEnumerable());
        setup
            .ChapterRecognition.FindAllChaptersAt(
                ingestPathB,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { chapterB }.ToAsyncEnumerable());

        await setup.Processor.ProcessAsync(lib, TestContext.Current.CancellationToken);

        setup
            .ChapterRecognition.Received(1)
            .FindAllChaptersAt(ingestPathA, lib.FilterRules, Arg.Any<CancellationToken>());
        setup
            .ChapterRecognition.Received(1)
            .FindAllChaptersAt(ingestPathB, lib.FilterRules, Arg.Any<CancellationToken>());
        setup.Cbz.Received(2).ConvertToCbz(Arg.Any<FoundChapter>(), Arg.Any<string>());

        List<Chapter> chapters = await db.Chapters.ToListAsync(
            TestContext.Current.CancellationToken
        );
        Assert.Equal(2, chapters.Count);
        Assert.Contains(chapters, c => c.FileName == "Chapter 1.cbz");
        Assert.Contains(chapters, c => c.FileName == "Chapter 2.cbz");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WithMissingIngestPath_SkipsItAndStillProcessesExistingPaths()
    {
        await using ApplicationDbContext db = _testDb.Context;
        IngestSetup setup = BuildIngestProcessor(db);

        string existingPath = Path.Combine(_tempRoot, "existing");
        string missingPath = Path.Combine(_tempRoot, "missing");
        Directory.CreateDirectory(existingPath);

        (Library lib, Manga manga) = await CreateLibraryAsync(
            db,
            "Missing Path Series",
            existingPath,
            missingPath
        );
        StubCommonDependencies(setup, lib, manga);

        FoundChapter chapter = Chapter("Missing Path Series", "Chapter 1.cbz", "Chapter 1", "1");
        setup
            .ChapterRecognition.FindAllChaptersAt(
                existingPath,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { chapter }.ToAsyncEnumerable());

        await setup.Processor.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // The missing path must be skipped before scanning is attempted.
        setup
            .ChapterRecognition.DidNotReceive()
            .FindAllChaptersAt(
                missingPath,
                Arg.Any<IReadOnlyList<LibraryFilterRule>?>(),
                Arg.Any<CancellationToken>()
            );

        List<Chapter> chapters = await db.Chapters.ToListAsync(
            TestContext.Current.CancellationToken
        );
        Assert.Single(chapters);
        Assert.Equal("Chapter 1.cbz", chapters[0].FileName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WhenOneIngestPathFails_ContinuesProcessingTheRemainingPaths()
    {
        await using ApplicationDbContext db = _testDb.Context;
        IngestSetup setup = BuildIngestProcessor(db);

        string failingPath = Path.Combine(_tempRoot, "failing");
        string workingPath = Path.Combine(_tempRoot, "working");
        Directory.CreateDirectory(failingPath);
        Directory.CreateDirectory(workingPath);

        (Library lib, Manga manga) = await CreateLibraryAsync(
            db,
            "Resilient Series",
            failingPath,
            workingPath
        );
        StubCommonDependencies(setup, lib, manga);

        FoundChapter chapter = Chapter("Resilient Series", "Chapter 1.cbz", "Chapter 1", "1");
        setup
            .ChapterRecognition.FindAllChaptersAt(
                failingPath,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new ThrowingAsyncEnumerable());
        setup
            .ChapterRecognition.FindAllChaptersAt(
                workingPath,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { chapter }.ToAsyncEnumerable());

        await setup.Processor.ProcessAsync(lib, TestContext.Current.CancellationToken);

        List<Chapter> chapters = await db.Chapters.ToListAsync(
            TestContext.Current.CancellationToken
        );
        Assert.Single(chapters);
        Assert.Equal("Chapter 1.cbz", chapters[0].FileName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WithDuplicateChaptersAcrossPaths_KeepsOnlyTheFirst()
    {
        await using ApplicationDbContext db = _testDb.Context;
        IngestSetup setup = BuildIngestProcessor(db);

        string pathA = Path.Combine(_tempRoot, "duplicateA");
        string pathB = Path.Combine(_tempRoot, "duplicateB");
        Directory.CreateDirectory(pathA);
        Directory.CreateDirectory(pathB);

        (Library lib, Manga manga) = await CreateLibraryAsync(db, "Duplicate Series", pathA, pathB);
        StubCommonDependencies(setup, lib, manga);

        FoundChapter duplicate = Chapter("Duplicate Series", "Chapter 1.cbz", "Chapter 1", "1");
        setup
            .ChapterRecognition.FindAllChaptersAt(
                pathA,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { duplicate }.ToAsyncEnumerable());
        setup
            .ChapterRecognition.FindAllChaptersAt(
                pathB,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { duplicate }.ToAsyncEnumerable());

        await setup.Processor.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // The second root resolves to the same target and must be skipped, not ingested twice.
        setup.Cbz.Received(1).ConvertToCbz(Arg.Any<FoundChapter>(), Arg.Any<string>());
        List<Chapter> chapters = await db.Chapters.ToListAsync(
            TestContext.Current.CancellationToken
        );
        Assert.Single(chapters);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WhenMergingAcrossPaths_ResolvesEachPartAgainstItsOwnRoot()
    {
        await using ApplicationDbContext db = _testDb.Context;
        IngestSetup setup = BuildIngestProcessor(db);

        string pathA = Path.Combine(_tempRoot, "mergeA");
        string pathB = Path.Combine(_tempRoot, "mergeB");
        Directory.CreateDirectory(pathA);
        Directory.CreateDirectory(pathB);

        (Library lib, Manga manga) = await CreateLibraryAsync(db, "Merge Series", pathA, pathB);
        lib.MergeChapterParts = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        StubCommonDependencies(setup, lib, manga);

        FoundChapter partA = Chapter("Merge Series", "Chapter 1.1.cbz", "Chapter 1.1", "1.1");
        FoundChapter partB = Chapter("Merge Series", "Chapter 1.2.cbz", "Chapter 1.2", "1.2");

        setup
            .ChapterRecognition.FindAllChaptersAt(
                pathA,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { partA }.ToAsyncEnumerable());
        setup
            .ChapterRecognition.FindAllChaptersAt(
                pathB,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { partB }.ToAsyncEnumerable());

        Func<FoundChapter, string>? getActualFilePath = null;
        setup
            .ChapterPartMerger.ProcessChapterMergingAsync(
                Arg.Any<List<FoundChapter>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<HashSet<string>>(),
                Arg.Do<Func<FoundChapter, string>>(f => getActualFilePath = f),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>()));

        await setup.Processor.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // Each part must resolve to its own ingest root, not the first root for both.
        Assert.NotNull(getActualFilePath);
        Assert.Equal(Path.Combine(pathA, partA.RelativePath), getActualFilePath!(partA));
        Assert.Equal(Path.Combine(pathB, partB.RelativePath), getActualFilePath!(partB));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ingest_WhenUpscaledChapterIsInAnotherPath_MovesItFromItsOwnRoot()
    {
        await using ApplicationDbContext db = _testDb.Context;
        IngestSetup setup = BuildIngestProcessor(db);

        string originalRoot = Path.Combine(_tempRoot, "originalRoot");
        string upscaledRoot = Path.Combine(_tempRoot, "upscaledRoot");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(upscaledRoot);

        (Library lib, Manga manga) = await CreateLibraryAsync(
            db,
            "Upscaled Series",
            originalRoot,
            upscaledRoot
        );
        StubCommonDependencies(setup, lib, manga);

        string relative = Path.Combine("Upscaled Series", "Chapter 1.cbz");
        FoundChapter chapter = Chapter("Upscaled Series", "Chapter 1.cbz", "Chapter 1", "1");

        // The already-upscaled file lives in the second root but shares the relative path.
        string upscaledFullPath = Path.Combine(upscaledRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledFullPath)!);
        await File.WriteAllTextAsync(
            upscaledFullPath,
            "fake",
            TestContext.Current.CancellationToken
        );

        setup
            .ChapterProcessing.DetectUpscaledFileAsync(
                upscaledFullPath,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult((true, (UpscalerProfileJsonDto?)null)));

        setup
            .ChapterRecognition.FindAllChaptersAt(
                originalRoot,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { chapter }.ToAsyncEnumerable());
        setup
            .ChapterRecognition.FindAllChaptersAt(
                upscaledRoot,
                lib.FilterRules,
                Arg.Any<CancellationToken>()
            )
            .Returns(new List<FoundChapter> { chapter }.ToAsyncEnumerable());

        await setup.Processor.ProcessAsync(lib, TestContext.Current.CancellationToken);

        // The upscaled file must be read/moved from the root it was actually found in.
        setup
            .FileSystem.Received(1)
            .Move(
                upscaledFullPath,
                Arg.Is<string>(target =>
                    target.StartsWith(lib.UpscaledLibraryPath!, StringComparison.Ordinal)
                )
            );
    }

    private sealed class ThrowingAsyncEnumerable : IAsyncEnumerable<FoundChapter>
    {
        public IAsyncEnumerator<FoundChapter> GetAsyncEnumerator(
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated ingest path failure");
    }
}
