using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Regression guard for <see cref="ApplyImageFiltersTask"/>: the chapter path properties dereference
/// <c>Manga.Library</c>, which relationship fixup only fills in while the chapters are tracked.
/// Loading them with <c>AsNoTracking()</c> left <c>Manga.Library</c> null, so every chapter threw a
/// NullReferenceException that the per-chapter catch swallowed — the task silently filtered nothing.
/// </summary>
public class ApplyImageFiltersTaskTests : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly ServiceProvider _provider;
    private readonly IImageFilterService _imageFilterService =
        Substitute.For<IImageFilterService>();

    public ApplyImageFiltersTaskTests()
    {
        _database = TestDatabaseFactory.Create();
        using (ApplicationDbContext schema = _database.CreateContext()) { }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        services.AddSingleton(_imageFilterService);
        _provider = services.BuildServiceProvider();

        _imageFilterService
            .ApplyFiltersToChapterAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<FilteredImage>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ImageFilterResult { FilteredCount = 0 });
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task ProcessAsync_ResolvesChapterPathsThroughMangaLibrary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string libraryRoot = Directory.CreateTempSubdirectory("apply-filters-").FullName;
        try
        {
            const string relativePath = "Series/Chapter 1.cbz";
            string fullPath = Path.Combine(libraryRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, "dummy", ct);

            int libraryId;
            await using (var setup = _provider.CreateAsyncScope())
            {
                var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var library = new Library
                {
                    Name = "Library",
                    NotUpscaledLibraryPath = libraryRoot,
                };
                library.FilteredImages.Add(
                    new FilteredImage { Library = library, OriginalFileName = "blocked.png" }
                );
                var manga = new Manga { PrimaryTitle = "Series", Library = library };
                manga.Chapters.Add(
                    new Chapter
                    {
                        Manga = manga,
                        FileName = "Chapter 1.cbz",
                        RelativePath = relativePath,
                    }
                );
                library.MangaSeries.Add(manga);
                db.Libraries.Add(library);
                await db.SaveChangesAsync(ct);
                libraryId = library.Id;
            }

            var task = new ApplyImageFiltersTask { LibraryId = libraryId, LibraryName = "Library" };
            await task.ProcessAsync(_provider, ct);

            // If fixup is broken the path dereference throws and is swallowed, so the filter service
            // is never called with the resolved path.
            await _imageFilterService
                .Received(1)
                .ApplyFiltersToChapterAsync(
                    fullPath,
                    null,
                    Arg.Any<IEnumerable<FilteredImage>>(),
                    Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }
}
