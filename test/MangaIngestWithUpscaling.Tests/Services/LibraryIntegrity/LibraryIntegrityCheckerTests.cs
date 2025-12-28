using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.CbzConversion;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace MangaIngestWithUpscaling.Tests.Services.LibraryIntegrity;

public class LibraryIntegrityCheckerTests : IDisposable
{
    private readonly SharedSqliteDb _db;
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly IMetadataHandlingService _metadata;
    private readonly IChapterInIngestRecognitionService _chapterRecognition;
    private readonly IUpscalerJsonHandlingService _upscalerJsonHandling;
    private readonly IFileSystem _fileSystem;
    private readonly IOptions<IntegrityCheckerConfig> _options;
    private readonly ITaskQueue _taskQueue;
    private readonly ICbzConverter _cbzConverter;
    private readonly ISplitProcessingCoordinator _splitCoordinator;

    public LibraryIntegrityCheckerTests()
    {
        _db = new SharedSqliteDb();
        _factory = new TestDbContextFactory(_db);
        _metadata = Substitute.For<IMetadataHandlingService>();
        _chapterRecognition = Substitute.For<IChapterInIngestRecognitionService>();
        _upscalerJsonHandling = Substitute.For<IUpscalerJsonHandlingService>();
        _fileSystem = Substitute.For<IFileSystem>();
        _taskQueue = Substitute.For<ITaskQueue>();
        _cbzConverter = Substitute.For<ICbzConverter>();
        _splitCoordinator = Substitute.For<ISplitProcessingCoordinator>();
        // Mock FixImageExtensionsInCbz to return false (no changes) by default
        _cbzConverter.FixImageExtensionsInCbz(Arg.Any<string>()).Returns(false);
        _options = Options.Create(new IntegrityCheckerConfig { MaxParallelism = 1 });
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_UpscaledValid_AlreadyMarked_OnlyFixesMetadata()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = true,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));
        // Provide valid metadata and allow WriteComicInfo to be called (no-op)
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(Task.FromResult(new ExtractedMetadata("Series", "Ch1", null)));
        _metadata
            .When(m => m.WriteComicInfoAsync(Arg.Any<string>(), Arg.Any<ExtractedMetadata>()))
            .Do(_ =>
            {
                /* no-op */
            });

        // Sanity: ensure upscaled file exists before running (valid upscale present)
        string upscaledPath = Path.Combine(lib.UpscaledLibraryPath!, rel);
        Assert.True(File.Exists(upscaledPath), "Test setup error: upscaled file missing");
        Assert.Equal(upscaledPath, chapter.UpscaledFullPath);

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        // Since IsUpscaled was already true and pages equal, we don't mark corrected; metadata could be fixed
        Assert.False(changed);
        Chapter reloaded = await ctx
            .Chapters.AsNoTracking()
            .FirstAsync(c => c.Id == chapter.Id, TestContext.Current.CancellationToken);
        Assert.True(reloaded.IsUpscaled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_ExistingUpscaleTask_ReturnsMaybeInProgress_NoTouch()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = false,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Seed an existing UpscaleTask for this chapter
        var dummyProfile = new UpscalerProfile
        {
            Name = "P",
            CompressionFormat = CompressionFormat.Avif,
            Quality = 50,
            ScalingFactor = ScaleFactor.TwoX,
        };
        var existingUpscale = new PersistedTask
        {
            Data = new UpscaleTask(chapter, dummyProfile),
            Order = 1,
        };
        ctx.PersistedTasks.Add(existingUpscale);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool result = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(result); // MaybeInProgress counts as needs attention
        Chapter reloaded = await ctx
            .Chapters.AsNoTracking()
            .FirstAsync(c => c.Id == chapter.Id, TestContext.Current.CancellationToken);
        Assert.False(reloaded.IsUpscaled);
        // No repair enqueued
#pragma warning disable xUnit1051
        await _taskQueue.DidNotReceive().EnqueueAsync(Arg.Any<RepairUpscaleTask>());
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task CheckIntegrity_ChapterMissing_RemovesFromDbAndReturnsTrue()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        // Arrange paths
        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = "Series/Ch1.cbz",
            Manga = manga,
            IsUpscaled = false,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub original metadata to avoid null and exception path
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        // Act - file does not exist
        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(changed);
        var inDb = await ctx.Chapters.FirstOrDefaultAsync(
            c => c.Id == chapter.Id,
            TestContext.Current.CancellationToken
        );
        Assert.Null(inDb);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_UpscaledValid_MarksUpscaledTrue()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = false,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub metadata handling
        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        // Act
        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(changed);
        var reloaded = await ctx
            .Chapters.AsNoTracking()
            .FirstAsync(c => c.Id == chapter.Id, TestContext.Current.CancellationToken);
        Assert.True(reloaded.IsUpscaled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_Library_ReportsTotalsAndCompletes()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        // Create 3 chapters whose files exist so they can be processed
        for (int i = 1; i <= 3; i++)
        {
            string rel = $"Series/Ch{i}.cbz";
            Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
            Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
            File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
            File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

            var ch = new Chapter
            {
                FileName = $"ch{i}.cbz",
                RelativePath = rel,
                Manga = manga,
                IsUpscaled = false,
            };
            manga.Chapters.Add(ch);
        }

        lib.MangaSeries.Add(manga);
        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub so that upscaled is considered valid and metadata OK
        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch", null));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        int? total = null;
        int? current = null;
        var reporter = new Progress<IntegrityProgress>(p =>
        {
            if (p.Total.HasValue)
            {
                total = p.Total;
            }

            if (p.Current.HasValue)
            {
                current = p.Current;
            }
        });

        // Act
        bool changed = await checker.CheckIntegrity(
            lib,
            reporter,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(changed);
        Assert.Equal(3, total);
        Assert.Equal(3, current);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_DifferentPages_WithProfile_EnqueuesRepair()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        // Create profile and attach to library
        var profile = new UpscalerProfile
        {
            Name = "Default",
            CompressionFormat = CompressionFormat.Avif,
            Quality = 80,
            ScalingFactor = ScaleFactor.TwoX,
        };
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
            UpscalerProfile = profile,
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = false,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Pages differ, but can repair
        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));
        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(new[] { "001.png" }, Array.Empty<string>()));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
#pragma warning disable xUnit1051 // Mocked API doesn't accept token
        await _taskQueue
            .Received(1)
            .EnqueueAsync(Arg.Is<RepairUpscaleTask>(t => t.ChapterId == chapter.Id));
#pragma warning restore xUnit1051
        // Ensure file not deleted
        Assert.True(File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_DifferentPages_NoProfile_DeletesUpscaled()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        // Library without profile; also ensure manga has no preference
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = true,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Pages differ and cannot repair (simulate by throwing from analyze or returning differences and then exception path triggers deletion)
        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));
        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(Array.Empty<string>(), new[] { "X.png" }));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        // Should have cleared IsUpscaled and deleted file
        await using (var verifyCtx = _db.CreateContext())
        {
            var reloaded = await verifyCtx
                .Chapters.AsNoTracking()
                .FirstAsync(c => c.Id == chapter.Id, TestContext.Current.CancellationToken);
            Assert.False(
                reloaded.IsUpscaled,
                "IsUpscaled should be cleared when upscaled file is missing"
            );
        }

        Assert.False(
            File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)),
            "Upscaled file should not exist"
        );
        await _taskQueue.DidNotReceiveWithAnyArgs().EnqueueAsync<RepairUpscaleTask>(default!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_DifferentPages_ExistingRepairTask_NoDuplicate()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var profile = new UpscalerProfile
        {
            Name = "Default",
            CompressionFormat = CompressionFormat.Avif,
            Quality = 80,
            ScalingFactor = ScaleFactor.TwoX,
        };
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
            UpscalerProfile = profile,
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = false,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Insert existing RepairUpscaleTask for this chapter
        var existing = new PersistedTask
        {
            Data = new RepairUpscaleTask(chapter, profile),
            Order = 1,
        };
        ctx.PersistedTasks.Add(existing);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));
        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(new[] { "001.png" }, Array.Empty<string>()));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);
        Assert.True(changed);

        // Should not enqueue a duplicate
#pragma warning disable xUnit1051
        await _taskQueue.DidNotReceive().EnqueueAsync(Arg.Any<RepairUpscaleTask>());
#pragma warning restore xUnit1051
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_UpscaledFileMissing_ClearsFlag()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        // Create only the original, upscaled is intentionally missing
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = true,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub original metadata to avoid nulls during original integrity check
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        await using (ApplicationDbContext verifyCtx = _db.CreateContext())
        {
            Chapter reloaded = await verifyCtx
                .Chapters.AsNoTracking()
                .FirstAsync(c => c.Id == chapter.Id, TestContext.Current.CancellationToken);
            Assert.False(reloaded.IsUpscaled);
        }

        Assert.False(File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)));
        await _taskQueue.DidNotReceiveWithAnyArgs().EnqueueAsync<RepairUpscaleTask>(default!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_DifferencesWithExtras_WithProfile_EnqueuesRepairAndKeepsFile()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var profile = new UpscalerProfile
        {
            Name = "Default",
            CompressionFormat = CompressionFormat.Avif,
            Quality = 80,
            ScalingFactor = ScaleFactor.TwoX,
        };
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
            UpscalerProfile = profile,
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = true,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Differences are repairable (extras can be removed)
        _metadata
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));
        _metadata
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));
        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(Array.Empty<string>(), new[] { "extra.png" }));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        // Should enqueue a repair task and keep the upscaled file
#pragma warning disable xUnit1051
        await _taskQueue
            .Received(1)
            .EnqueueAsync(Arg.Is<RepairUpscaleTask>(t => t.ChapterId == chapter.Id));
#pragma warning restore xUnit1051
        Assert.True(File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OriginalMissing_RemovesChapter_DeletesUpscaledIfExists()
    {
        await using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        // Do NOT create original; create only upscaled
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz",
            RelativePath = rel,
            Manga = manga,
            IsUpscaled = true,
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        var inDb = await ctx.Chapters.FirstOrDefaultAsync(
            c => c.Id == chapter.Id,
            TestContext.Current.CancellationToken
        );
        Assert.Null(inDb);
        Assert.False(File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)));
        await _taskQueue.DidNotReceiveWithAnyArgs().EnqueueAsync<RepairUpscaleTask>(default!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedFile_CreatesChapterEntity()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);

        // Create manga series
        var manga = new Manga
        {
            PrimaryTitle = "Test Series",
            LibraryId = lib.Id,
            Library = lib,
        };
        ctx.MangaSeries.Add(manga);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create an orphaned file (file exists but no chapter entity)
        string testFileName = "Chapter 001.cbz";
        string testRelativePath = Path.Join("Test Series", testFileName);
        string testFullPath = Path.Combine(temp, testRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(testFullPath)!);
        await File.WriteAllTextAsync(
            testFullPath,
            "test content",
            TestContext.Current.CancellationToken
        );

        // Setup mock to return the found chapter
        var foundChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                testRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { foundChapter }.ToAsyncEnumerable());

        // Setup upscaler service to return null (no upscaler profile)
        _upscalerJsonHandling
            .ReadUpscalerJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpscalerProfileJsonDto?>(null));

        // Initially no chapters should exist
        Assert.Equal(0, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have detected and created the missing chapter entity
        Assert.True(changed);
        Assert.Equal(1, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var createdChapter = await ctx.Chapters.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(testFileName, createdChapter.FileName);
        Assert.Equal(testRelativePath, createdChapter.RelativePath);
        Assert.Equal(manga.Id, createdChapter.MangaId);
        Assert.False(createdChapter.IsUpscaled); // Should be false since no upscaled variant

        // Clean up
        Directory.Delete(temp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedOriginalAndUpscaledFiles_CreatesSingleChapterEntity()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        string upscaledTemp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            UpscaledLibraryPath = upscaledTemp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);

        // Create manga series
        var manga = new Manga
        {
            PrimaryTitle = "Test Series",
            LibraryId = lib.Id,
            Library = lib,
        };
        ctx.MangaSeries.Add(manga);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create orphaned original file
        string testFileName = "Chapter 001.cbz";
        string testRelativePath = Path.Join("Test Series", testFileName);
        string testFullPath = Path.Combine(temp, testRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(testFullPath)!);
        await File.WriteAllTextAsync(
            testFullPath,
            "original content",
            TestContext.Current.CancellationToken
        );

        // Create orphaned upscaled file
        string upscaledRelativePath = Path.Join("Test Series", testFileName);
        string upscaledFullPath = Path.Combine(upscaledTemp, upscaledRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(upscaledFullPath)!);
        await File.WriteAllTextAsync(
            upscaledFullPath,
            "upscaled content",
            TestContext.Current.CancellationToken
        );

        // Setup mocks to return both found chapters
        var originalChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                testRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        var upscaledChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                upscaledRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { originalChapter }.ToAsyncEnumerable());

        _chapterRecognition
            .FindAllChaptersAt(upscaledTemp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { upscaledChapter }.ToAsyncEnumerable());

        // Setup upscaler service to return null (no upscaler profile)
        _upscalerJsonHandling
            .ReadUpscalerJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpscalerProfileJsonDto?>(null));

        // Initially no chapters should exist
        Assert.Equal(0, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have detected and created a single chapter entity representing both files
        Assert.True(changed);
        Assert.Equal(1, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var createdChapter = await ctx.Chapters.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(testFileName, createdChapter.FileName);
        Assert.Equal(testRelativePath, createdChapter.RelativePath); // Should use canonical path
        Assert.Equal(manga.Id, createdChapter.MangaId);
        Assert.True(createdChapter.IsUpscaled); // Should be true since upscaled variant exists

        // Clean up
        Directory.Delete(temp, true);
        Directory.Delete(upscaledTemp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedUpscaledFileWithUpscalerJson_CreatesChapterAndMoves()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        string upscaledTemp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            UpscaledLibraryPath = upscaledTemp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);

        // Create manga series
        var manga = new Manga
        {
            PrimaryTitle = "Test Series",
            LibraryId = lib.Id,
            Library = lib,
        };
        ctx.MangaSeries.Add(manga);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create an existing original chapter entity
        var originalChapter = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 001.cbz",
            RelativePath = Path.Join("Test Series", "Chapter 001.cbz"),
            IsUpscaled = false,
        };
        ctx.Chapters.Add(originalChapter);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create orphaned upscaled file in the wrong location (normal library path) with upscaler.json
        string testFileName = "Chapter 001.cbz";
        string testRelativePath = Path.Join("Test Series", testFileName);
        string testFullPath = Path.Combine(temp, testRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(testFullPath)!);
        await File.WriteAllTextAsync(
            testFullPath,
            "upscaled content",
            TestContext.Current.CancellationToken
        );

        // Setup mocks to return the found upscaled chapter
        var upscaledChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                testRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { upscaledChapter }.ToAsyncEnumerable());

        _chapterRecognition
            .FindAllChaptersAt(upscaledTemp, null, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<FoundChapter>());

        // Setup upscaler service to return upscaler profile for the upscaled file
        var upscalerProfileDto = new UpscalerProfileJsonDto
        {
            Name = "Test Profile",
            UpscalerMethod = UpscalerMethod.MangaJaNai,
            ScalingFactor = 2,
            CompressionFormat = CompressionFormat.Avif,
            Quality = 80,
        };
        _upscalerJsonHandling
            .ReadUpscalerJsonAsync(testFullPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpscalerProfileJsonDto?>(upscalerProfileDto));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have detected the upscaled file and updated the existing chapter
        Assert.True(changed);

        // Reload the original chapter to check if it was updated
        await ctx.Entry(originalChapter).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(originalChapter.IsUpscaled); // Should now be marked as upscaled

        // Should have attempted to move the file to the upscaled library path
        string expectedTargetPath = Path.Combine(upscaledTemp, testRelativePath);
        _fileSystem.Received(1).Move(testFullPath, expectedTargetPath);

        // Clean up
        Directory.Delete(temp, true);
        Directory.Delete(upscaledTemp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedUpscaledFileWithoutOriginal_SkipsProcessing()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        string upscaledTemp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            UpscaledLibraryPath = upscaledTemp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);

        // Create manga series but NO original chapter
        var manga = new Manga
        {
            PrimaryTitle = "Test Series",
            LibraryId = lib.Id,
            Library = lib,
        };
        ctx.MangaSeries.Add(manga);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create orphaned upscaled file in upscaled library path
        string testFileName = "Chapter 001.cbz";
        string testRelativePath = Path.Join("Test Series", testFileName);
        string upscaledFullPath = Path.Combine(upscaledTemp, testRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(upscaledFullPath)!);
        await File.WriteAllTextAsync(
            upscaledFullPath,
            "upscaled content",
            TestContext.Current.CancellationToken
        );

        // Setup mocks to return only the upscaled chapter
        var upscaledChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                testRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<FoundChapter>());

        _chapterRecognition
            .FindAllChaptersAt(upscaledTemp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { upscaledChapter }.ToAsyncEnumerable());

        // Setup upscaler service to return null (no upscaler profile)
        _upscalerJsonHandling
            .ReadUpscalerJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpscalerProfileJsonDto?>(null));

        // Initially only manga exists, no chapters
        Assert.Equal(0, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should NOT have created a chapter entity because there's no original
        Assert.False(changed);
        Assert.Equal(0, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        // Should not have tried to move any files
        _fileSystem.DidNotReceiveWithAnyArgs().Move(Arg.Any<string>(), Arg.Any<string>());

        // Clean up
        Directory.Delete(temp, true);
        Directory.Delete(upscaledTemp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedUpscaledFileInUpscaledFolder_DetectedByPath()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        string upscaledTemp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            UpscaledLibraryPath = upscaledTemp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);

        // Create manga series
        var manga = new Manga
        {
            PrimaryTitle = "Test Series",
            LibraryId = lib.Id,
            Library = lib,
        };
        ctx.MangaSeries.Add(manga);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create orphaned original and upscaled files (both orphaned)
        string testFileName = "Chapter 001.cbz";
        string testRelativePath = Path.Join("Test Series", testFileName);

        // Original file
        string originalFullPath = Path.Combine(temp, testRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalFullPath)!);
        await File.WriteAllTextAsync(
            originalFullPath,
            "original content",
            TestContext.Current.CancellationToken
        );

        // Upscaled file in upscaled library path
        string upscaledFullPath = Path.Combine(upscaledTemp, testRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledFullPath)!);
        await File.WriteAllTextAsync(
            upscaledFullPath,
            "upscaled content",
            TestContext.Current.CancellationToken
        );

        // Setup mocks to return both chapters
        var originalChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                testRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        var upscaledChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                testFileName,
                testRelativePath,
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { originalChapter }.ToAsyncEnumerable());

        _chapterRecognition
            .FindAllChaptersAt(upscaledTemp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { upscaledChapter }.ToAsyncEnumerable());

        // Setup upscaler service to return null for original, profile for upscaled
        _upscalerJsonHandling
            .ReadUpscalerJsonAsync(originalFullPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpscalerProfileJsonDto?>(null));
        _upscalerJsonHandling
            .ReadUpscalerJsonAsync(upscaledFullPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpscalerProfileJsonDto?>(null));

        // Initially no chapters should exist
        Assert.Equal(0, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have detected and created a single chapter entity representing both files
        Assert.True(changed);
        Assert.Equal(1, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var createdChapter = await ctx.Chapters.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(testFileName, createdChapter.FileName);
        Assert.Equal(testRelativePath, createdChapter.RelativePath);
        Assert.Equal(manga.Id, createdChapter.MangaId);
        Assert.True(createdChapter.IsUpscaled); // Should be true since upscaled variant exists

        // Should not have moved any files (upscaled file is already in correct location)
        _fileSystem.DidNotReceiveWithAnyArgs().Move(Arg.Any<string>(), Arg.Any<string>());

        // Clean up
        Directory.Delete(temp, true);
        Directory.Delete(upscaledTemp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedFileWithFileMovement_MovesFileToCorrectLocation()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create orphaned file in wrong location (no series directory structure)
        string wrongLocationFile = Path.Combine(temp, "Chapter 001.cbz");
        await File.WriteAllTextAsync(
            wrongLocationFile,
            "test content",
            TestContext.Current.CancellationToken
        );

        // Setup mocks to return the orphaned chapter with correct series name
        var orphanedChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                "Chapter 001.cbz",
                "Chapter 001.cbz", // No series folder in path
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Correct Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { orphanedChapter }.ToAsyncEnumerable());

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have moved file and created chapter entity
        Assert.True(changed);
        Assert.Equal(1, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var createdChapter = await ctx
            .Chapters.Include(c => c.Manga)
            .FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Correct Series", createdChapter.Manga.PrimaryTitle);
        Assert.Equal("Correct Series/Chapter 001.cbz", createdChapter.RelativePath);

        // Verify file was moved to correct location
        Assert.False(File.Exists(wrongLocationFile));
        Assert.True(File.Exists(Path.Combine(temp, "Correct Series", "Chapter 001.cbz")));

        // Clean up
        Directory.Delete(temp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedFileWithDuplicate_DeletesOrphanedFile()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);

        // Create existing manga and chapter entity
        var manga = new Manga
        {
            PrimaryTitle = "Test Series",
            LibraryId = lib.Id,
            Library = lib,
        };
        ctx.MangaSeries.Add(manga);

        var existingChapter = new Chapter
        {
            FileName = "Chapter 001.cbz",
            RelativePath = "Test Series/Chapter 001.cbz",
            Manga = manga,
            MangaId = manga.Id,
            IsUpscaled = false,
        };
        ctx.Chapters.Add(existingChapter);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create the legitimate file
        string legitimateFile = Path.Combine(temp, "Test Series", "Chapter 001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(legitimateFile)!);
        await File.WriteAllTextAsync(
            legitimateFile,
            "legitimate content",
            TestContext.Current.CancellationToken
        );

        // Create orphaned duplicate file in wrong location
        string orphanedFile = Path.Combine(temp, "Wrong Location", "Chapter 001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanedFile)!);
        await File.WriteAllTextAsync(
            orphanedFile,
            "orphaned content",
            TestContext.Current.CancellationToken
        );

        var orphanedChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                "Chapter 001.cbz",
                Path.GetRelativePath(temp, orphanedFile),
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Test Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { orphanedChapter }.ToAsyncEnumerable());

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have detected and deleted duplicate
        Assert.True(changed);
        Assert.Equal(1, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken)); // Still only one chapter

        // Verify orphaned file was deleted, legitimate file remains
        Assert.False(File.Exists(orphanedFile));
        Assert.True(File.Exists(legitimateFile));

        // Clean up
        Directory.Delete(temp, true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckIntegrity_OrphanedFileWithMetadataFix_FixesSeriesTitle()
    {
        using ApplicationDbContext ctx = _db.CreateContext();

        string temp = Directory.CreateTempSubdirectory().FullName;
        var lib = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = temp,
            IngestPath = temp,
        };
        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create orphaned file with wrong series title in folder structure
        string orphanedFile = Path.Combine(temp, "Wrong Series Name", "Chapter 001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanedFile)!);
        await File.WriteAllTextAsync(
            orphanedFile,
            "content",
            TestContext.Current.CancellationToken
        );

        var testChapter =
            new MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.FoundChapter(
                "Chapter 001.cbz",
                Path.GetRelativePath(temp, orphanedFile),
                MangaIngestWithUpscaling.Shared.Services.ChapterRecognition.ChapterStorageType.Cbz,
                new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                    "Correct Series",
                    "Chapter 001",
                    "001"
                )
            );

        _chapterRecognition
            .FindAllChaptersAt(temp, null, Arg.Any<CancellationToken>())
            .Returns(new[] { testChapter }.ToAsyncEnumerable());

        // Mock metadata reading to return wrong series title initially
        var wrongMetadata =
            new MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata(
                "Wrong Series Name",
                "Chapter 001",
                "001"
            );
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>()).Returns(wrongMetadata);

        var checker = new LibraryIntegrityChecker(
            ctx,
            _factory,
            _metadata,
            _chapterRecognition,
            new ChapterProcessingService(
                ctx,
                _upscalerJsonHandling,
                _fileSystem,
                Substitute.For<IStringLocalizer<ChapterProcessingService>>(),
                NullLogger<ChapterProcessingService>.Instance
            ),
            _taskQueue,
            _cbzConverter,
            NullLogger<LibraryIntegrityChecker>.Instance,
            _options,
            _splitCoordinator,
            Substitute.For<IStringLocalizer<LibraryIntegrityChecker>>()
        );

        bool changed = await checker.CheckIntegrity(lib, TestContext.Current.CancellationToken);

        // Should have created chapter with correct series title
        Assert.True(changed);
        Assert.Equal(1, await ctx.Chapters.CountAsync(TestContext.Current.CancellationToken));

        var createdChapter = await ctx
            .Chapters.Include(c => c.Manga)
            .FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Correct Series", createdChapter.Manga.PrimaryTitle);

        // Verify metadata was written with corrected series title
        await _metadata
            .Received(1)
            .WriteComicInfoAsync(
                Arg.Any<string>(),
                Arg.Is<MangaIngestWithUpscaling.Shared.Services.MetadataHandling.ExtractedMetadata>(
                    m => m.Series == "Correct Series"
                )
            );

        // Clean up
        Directory.Delete(temp, true);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly SharedSqliteDb _db;

        public TestDbContextFactory(SharedSqliteDb db)
        {
            _db = db;
        }

        public ApplicationDbContext CreateDbContext()
        {
            return _db.CreateContext();
        }

        public Task<ApplicationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(_db.CreateContext());
        }
    }

    private sealed class SharedSqliteDb : IDisposable
    {
        private readonly string _connectionString;
        private readonly bool _initialized;
        private readonly SqliteConnection _keeper; // keeps the shared in-memory DB alive

        public SharedSqliteDb()
        {
            // Use a uniquely named shared in-memory database so multiple test instances don't collide
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"file:integrity-tests-{Guid.NewGuid():N}?mode=memory&cache=shared",
            }.ToString();

            _keeper = new SqliteConnection(_connectionString);
            _keeper.Open();

            using ApplicationDbContext ctx = CreateContext();
            if (!_initialized)
            {
                ctx.Database.EnsureCreated();
                _initialized = true;
            }
        }

        public void Dispose()
        {
            _keeper.Close();
            _keeper.Dispose();
        }

        public ApplicationDbContext CreateContext()
        {
            // Create a fresh connection per context to avoid function registration conflicts
            var conn = new SqliteConnection(_connectionString);
            conn.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(conn)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
                .Options;
            return new ApplicationDbContext(options);
        }
    }
}
