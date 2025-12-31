using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using TestContext = Xunit.TestContext;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

[Collection(TestDatabaseCollection.Name)]
public class SplitApplicationServiceTests
{
    private readonly TestDatabaseFixture _fixture;

    public SplitApplicationServiceTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<TestDatabaseBackend> Backends => TestDatabaseBackends.Enabled;

    private sealed class TestScope : IAsyncDisposable
    {
        public TestScope(
            TestDatabase database,
            ApplicationDbContext dbContext,
            SplitApplicationService service,
            ISplitProcessingCoordinator coordinator,
            ISplitApplier splitApplier,
            IUpscaler upscaler,
            ILogger<SplitApplicationService> logger,
            string tempDir
        )
        {
            Database = database;
            DbContext = dbContext;
            Service = service;
            Coordinator = coordinator;
            SplitApplier = splitApplier;
            Upscaler = upscaler;
            Logger = logger;
            TempDir = tempDir;
        }

        public TestDatabase Database { get; }

        public ApplicationDbContext DbContext { get; }

        public SplitApplicationService Service { get; }

        public ISplitProcessingCoordinator Coordinator { get; }

        public ISplitApplier SplitApplier { get; }

        public IUpscaler Upscaler { get; }

        public ILogger<SplitApplicationService> Logger { get; }

        public string TempDir { get; }

        public async ValueTask DisposeAsync()
        {
            DbContext.Dispose();
            await Database.DisposeAsync();

            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, true);
            }
        }
    }

    private async Task<TestScope> CreateScopeAsync(TestDatabaseBackend backend)
    {
        var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        var context = await database.CreateContextAsync(TestContext.Current.CancellationToken);

        var coordinator = Substitute.For<ISplitProcessingCoordinator>();
        var splitApplier = Substitute.For<ISplitApplier>();
        var upscaler = Substitute.For<IUpscaler>();
        var logger = Substitute.For<ILogger<SplitApplicationService>>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"split_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        var service = new SplitApplicationService(
            context,
            coordinator,
            splitApplier,
            upscaler,
            logger,
            Substitute.For<IStringLocalizer<SplitApplicationService>>()
        );

        return new TestScope(
            database,
            context,
            service,
            coordinator,
            splitApplier,
            upscaler,
            logger,
            tempDir
        );
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ApplySplitsAsync_WhenChapterIsUpscaled_UpscalesSplitPagesOnly(
        TestDatabaseBackend backend
    )
    {
        await using var scope = await CreateScopeAsync(backend);
        var dbContext = scope.DbContext;

        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(scope.TempDir, "original"),
            UpscaledLibraryPath = Path.Combine(scope.TempDir, "upscaled"),
        };
        dbContext.Libraries.Add(library);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath);

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        dbContext.UpscalerProfiles.Add(profile);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        dbContext.MangaSeries.Add(manga);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = true,
            UpscalerProfileId = profile.Id,
            UpscalerProfile = profile,
        };
        dbContext.Chapters.Add(chapter);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ with test images (one will be split, one won't)
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(scope.TempDir, originalCbzPath, "page1.png", "page2.png");

        // Create upscaled CBZ with same pages
        var upscaledCbzPath = chapter.UpscaledFullPath!;
        CreateTestCbz(scope.TempDir, upscaledCbzPath, "page1.png", "page2.png");

        // Create split finding for page1 only
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page1",
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        dbContext.StripSplitFindings.Add(finding);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier to create dummy split files
        scope
            .SplitApplier.ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                var part2 = Path.Combine(outputDir, "page1_part2.png");
                File.WriteAllText(part1, "dummy part1");
                File.WriteAllText(part2, "dummy part2");
                return new List<string> { part1, part2 };
            });

        // Configure upscaler to create dummy upscaled CBZ
        scope
            .Upscaler.Upscale(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<UpscalerProfile>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                var outputPath = callInfo.ArgAt<string>(1);
                // Create a CBZ with upscaled split pages
                CreateTestCbz(scope.TempDir, outputPath, "page1_part1.png", "page1_part2.png");
                return Task.CompletedTask;
            });

        await scope.Service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Verify upscaler was called to upscale the split pages
        await scope
            .Upscaler.Received(1)
            .Upscale(Arg.Any<string>(), Arg.Any<string>(), profile, Arg.Any<CancellationToken>());

        // Verify the upscaled CBZ still exists and has been updated
        Assert.True(File.Exists(upscaledCbzPath), "Upscaled CBZ should still exist");

        // Chapter should remain upscaled
        await dbContext.Entry(chapter).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(chapter.IsUpscaled, "Chapter should still be marked as upscaled");
        Assert.Equal(profile.Id, chapter.UpscalerProfileId);

        // Verify coordinator was notified
        await scope
            .Coordinator.Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ApplySplitsAsync_WhenChapterIsNotUpscaled_DoesNotAttemptToDeleteUpscaled(
        TestDatabaseBackend backend
    )
    {
        await using var scope = await CreateScopeAsync(backend);
        var dbContext = scope.DbContext;

        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(scope.TempDir, "original"),
        };
        dbContext.Libraries.Add(library);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        dbContext.MangaSeries.Add(manga);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = false,
        };
        dbContext.Chapters.Add(chapter);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(scope.TempDir, originalCbzPath, "page1.png");

        // Create split finding
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page1",
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        dbContext.StripSplitFindings.Add(finding);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier
        scope
            .SplitApplier.ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                var part2 = Path.Combine(outputDir, "page1_part2.png");
                File.WriteAllText(part1, "dummy");
                File.WriteAllText(part2, "dummy");
                return new List<string> { part1, part2 };
            });

        await scope.Service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        await dbContext.Entry(chapter).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(chapter.IsUpscaled);

        await scope
            .Coordinator.Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    private static void CreateTestCbz(string baseTempDir, string path, params string[] imageNames)
    {
        var tempExtractDir = Path.Combine(baseTempDir, $"extract_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            // Create dummy image files
            foreach (var imageName in imageNames)
            {
                var imagePath = Path.Combine(tempExtractDir, imageName);
                File.WriteAllText(imagePath, $"dummy image content for {imageName}");
            }

            // Create CBZ (which is just a ZIP file)
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            ZipFile.CreateFromDirectory(tempExtractDir, path);
        }
        finally
        {
            if (Directory.Exists(tempExtractDir))
            {
                Directory.Delete(tempExtractDir, true);
            }
        }
    }
}
