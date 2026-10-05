using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Regression guard for <see cref="RepairUpscaleTask"/>: when the original archive is missing,
/// <c>AnalyzePageDifferencesAsync</c> reads it as an empty archive, so every upscaled page looks like
/// an extra page. The repair then rebuilt the upscaled CBZ from nothing, and the failure handler
/// deleted it. A missing source must fail without touching the upscaled file.
/// </summary>
public class RepairUpscaleTaskTests : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly ServiceProvider _provider;

    public RepairUpscaleTaskTests()
    {
        _database = TestDatabaseFactory.Create();
        using (ApplicationDbContext schema = _database.CreateContext()) { }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        services.AddSingleton(Substitute.For<IMangaMetadataChanger>());
        services.AddSingleton(Substitute.For<IMetadataHandlingService>());
        services.AddSingleton(Substitute.For<IChapterChangedNotifier>());
        services.AddSingleton(Substitute.For<IUpscaler>());
        services.AddSingleton(Substitute.For<IRepairService>());
        services.AddSingleton(Substitute.For<IStringLocalizer<RepairUpscaleTask>>());
        _provider = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessAsync_WhenOriginalArchiveIsMissing_DoesNotDeleteTheUpscaledCbz()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string notUpscaledRoot = Directory.CreateTempSubdirectory("repair-original-").FullName;
        string upscaledRoot = Directory.CreateTempSubdirectory("repair-upscaled-").FullName;
        try
        {
            const string relativePath = "Series/Chapter 1.cbz";
            string upscaledPath = Path.Combine(upscaledRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(upscaledPath)!);
            byte[] upscaledContent = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(upscaledPath, upscaledContent, ct);
            // The original archive is deliberately absent.

            int chapterId;
            int profileId;
            await using (var setup = _provider.CreateAsyncScope())
            {
                var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var profile = new UpscalerProfile
                {
                    Name = "Profile",
                    ScalingFactor = ScaleFactor.TwoX,
                    CompressionFormat = CompressionFormat.Png,
                    Quality = 90,
                };
                var library = new Library
                {
                    Name = "Library",
                    NotUpscaledLibraryPath = notUpscaledRoot,
                    UpscaledLibraryPath = upscaledRoot,
                };
                var manga = new Manga
                {
                    PrimaryTitle = "Series",
                    Library = library,
                    UpscalerProfilePreference = profile,
                };
                var chapter = new Chapter
                {
                    Manga = manga,
                    FileName = "Chapter 1.cbz",
                    RelativePath = relativePath,
                    UpscalerProfile = profile,
                };
                manga.Chapters.Add(chapter);
                library.MangaSeries.Add(manga);
                db.Libraries.Add(library);
                db.UpscalerProfiles.Add(profile);
                await db.SaveChangesAsync(ct);
                chapterId = chapter.Id;
                profileId = profile.Id;
            }

            var task = new RepairUpscaleTask
            {
                ChapterId = chapterId,
                UpscalerProfileId = profileId,
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() => task.ProcessAsync(_provider, ct));

            Assert.True(File.Exists(upscaledPath));
            Assert.Equal(upscaledContent, await File.ReadAllBytesAsync(upscaledPath, ct));
        }
        finally
        {
            Directory.Delete(notUpscaledRoot, recursive: true);
            Directory.Delete(upscaledRoot, recursive: true);
        }
    }
}
