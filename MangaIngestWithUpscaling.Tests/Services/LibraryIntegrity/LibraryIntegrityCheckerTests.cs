using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly IOptions<IntegrityCheckerConfig> _options;
    private readonly ITaskQueue _taskQueue;

    public LibraryIntegrityCheckerTests()
    {
        _db = new SharedSqliteDb();
        _factory = new TestDbContextFactory(_db);
        _metadata = Substitute.For<IMetadataHandlingService>();
        _taskQueue = Substitute.For<ITaskQueue>();
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = true };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        // Provide valid metadata and allow WriteComicInfo to be called (no-op)
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
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

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        // Since IsUpscaled was already true and pages equal, we don't mark corrected; metadata could be fixed
        Assert.False(changed);
        Chapter reloaded = await ctx.Chapters.AsNoTracking()
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = false };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Seed an existing UpscaleTask for this chapter
        var dummyProfile = new UpscalerProfile
        {
            Name = "P", CompressionFormat = CompressionFormat.Avif, Quality = 50, ScalingFactor = ScaleFactor.TwoX
        };
        var existingUpscale = new PersistedTask { Data = new UpscaleTask(chapter, dummyProfile), Order = 1 };
        ctx.PersistedTasks.Add(existingUpscale);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool result = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(result); // MaybeInProgress counts as needs attention
        Chapter reloaded = await ctx.Chapters.AsNoTracking()
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter
        {
            FileName = "ch1.cbz", RelativePath = "Series/Ch1.cbz", Manga = manga, IsUpscaled = false
        };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub original metadata to avoid null and exception path
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        // Act - file does not exist
        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(changed);
        var inDb = await ctx.Chapters.FirstOrDefaultAsync(c => c.Id == chapter.Id,
            TestContext.Current.CancellationToken);
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = false };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub metadata handling
        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        // Act
        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(changed);
        var reloaded = await ctx.Chapters.AsNoTracking()
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
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

            var ch = new Chapter { FileName = $"ch{i}.cbz", RelativePath = rel, Manga = manga, IsUpscaled = false };
            manga.Chapters.Add(ch);
        }

        lib.MangaSeries.Add(manga);
        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub so that upscaled is considered valid and metadata OK
        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch", null));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

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
        bool changed = await checker.CheckIntegrity(lib, reporter, TestContext.Current.CancellationToken);

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
            ScalingFactor = ScaleFactor.TwoX
        };
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
            UpscalerProfile = profile
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = false };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Pages differ, but can repair
        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));
        _metadata.AnalyzePageDifferences(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(new[] { "001.png" }, Array.Empty<string>()));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
#pragma warning disable xUnit1051 // Mocked API doesn't accept token
        await _taskQueue.Received(1).EnqueueAsync(Arg.Is<RepairUpscaleTask>(t => t.ChapterId == chapter.Id));
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = true };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Pages differ and cannot repair (simulate by throwing from analyze or returning differences and then exception path triggers deletion)
        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));
        _metadata.AnalyzePageDifferences(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(Array.Empty<string>(), new[] { "X.png" }));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        // Should have cleared IsUpscaled and deleted file
        await using (var verifyCtx = _db.CreateContext())
        {
            var reloaded = await verifyCtx.Chapters.AsNoTracking()
                .FirstAsync(c => c.Id == chapter.Id, TestContext.Current.CancellationToken);
            Assert.False(reloaded.IsUpscaled, "IsUpscaled should be cleared when upscaled file is missing");
        }

        Assert.False(File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)), "Upscaled file should not exist");
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
            ScalingFactor = ScaleFactor.TwoX
        };
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
            UpscalerProfile = profile
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = false };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Insert existing RepairUpscaleTask for this chapter
        var existing = new PersistedTask { Data = new RepairUpscaleTask(chapter, profile), Order = 1 };
        ctx.PersistedTasks.Add(existing);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _metadata.AnalyzePageDifferences(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(new[] { "001.png" }, Array.Empty<string>()));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        // Create only the original, upscaled is intentionally missing
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = true };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Stub original metadata to avoid nulls during original integrity check
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        await using (ApplicationDbContext verifyCtx = _db.CreateContext())
        {
            Chapter reloaded = await verifyCtx.Chapters.AsNoTracking()
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
            ScalingFactor = ScaleFactor.TwoX
        };
        var lib = new Library
        {
            Name = "Lib",
            NotUpscaledLibraryPath = Path.Combine(temp, "orig"),
            UpscaledLibraryPath = Path.Combine(temp, "up"),
            UpscalerProfile = profile
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        File.WriteAllText(Path.Combine(lib.NotUpscaledLibraryPath, rel), "orig");
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = true };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Differences are repairable (extras can be removed)
        _metadata.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _metadata.GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(new ExtractedMetadata("Series", "Ch1", null));
        _metadata.AnalyzePageDifferences(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new PageDifferenceResult(Array.Empty<string>(), new[] { "extra.png" }));

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        // Should enqueue a repair task and keep the upscaled file
#pragma warning disable xUnit1051
        await _taskQueue.Received(1).EnqueueAsync(Arg.Is<RepairUpscaleTask>(t => t.ChapterId == chapter.Id));
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
            UpscaledLibraryPath = Path.Combine(temp, "up")
        };
        Directory.CreateDirectory(lib.NotUpscaledLibraryPath);
        Directory.CreateDirectory(lib.UpscaledLibraryPath!);

        string rel = "Series/Ch1.cbz";
        Directory.CreateDirectory(Path.Combine(lib.NotUpscaledLibraryPath, "Series"));
        Directory.CreateDirectory(Path.Combine(lib.UpscaledLibraryPath!, "Series"));
        // Do NOT create original; create only upscaled
        File.WriteAllText(Path.Combine(lib.UpscaledLibraryPath!, rel), "up");

        var manga = new Manga { PrimaryTitle = "Series", Library = lib };
        var chapter = new Chapter { FileName = "ch1.cbz", RelativePath = rel, Manga = manga, IsUpscaled = true };
        manga.Chapters.Add(chapter);
        lib.MangaSeries.Add(manga);

        ctx.Libraries.Add(lib);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        var checker = new LibraryIntegrityChecker(ctx, _factory, _metadata, _taskQueue,
            NullLogger<LibraryIntegrityChecker>.Instance, _options);

        bool changed = await checker.CheckIntegrity(chapter, TestContext.Current.CancellationToken);

        Assert.True(changed);
        var inDb = await ctx.Chapters.FirstOrDefaultAsync(c => c.Id == chapter.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(inDb);
        Assert.False(File.Exists(Path.Combine(lib.UpscaledLibraryPath!, rel)));
        await _taskQueue.DidNotReceiveWithAnyArgs().EnqueueAsync<RepairUpscaleTask>(default!);
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

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
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
                DataSource = $"file:integrity-tests-{Guid.NewGuid():N}?mode=memory&cache=shared"
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