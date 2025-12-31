using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TestContext = Xunit.TestContext;

namespace MangaIngestWithUpscaling.Tests.Data;

[Collection(TestDatabaseCollection.Name)]
public class EntityTimestampTests
{
    private readonly TestDatabaseFixture _fixture;

    public EntityTimestampTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<TestDatabaseBackend> Backends => TestDatabaseBackends.Enabled;

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Library_CreatedAt_ShouldBeSetOnCreation(TestDatabaseBackend backend)
    {
        await using var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        await using var context = await database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        // Arrange
        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled",
        };

        // Act
        context.Libraries.Add(library);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(library.CreatedAt >= beforeCreation);
        Assert.True(library.CreatedAt <= afterCreation);
        Assert.True(library.ModifiedAt >= beforeCreation);
        Assert.True(library.ModifiedAt <= afterCreation);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Library_ModifiedAt_ShouldBeUpdatedOnModification(TestDatabaseBackend backend)
    {
        await using var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        await using var context = await database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled",
        };
        context.Libraries.Add(library);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051

        var originalModifiedAt = library.ModifiedAt;

        // Wait a moment to ensure time difference
#pragma warning disable xUnit1051
        await Task.Delay(100);
#pragma warning restore xUnit1051
        var beforeModification = DateTime.UtcNow;

        // Act
        library.Name = "Modified Library";
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051
        var afterModification = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(library.ModifiedAt > originalModifiedAt);
        Assert.True(library.ModifiedAt >= beforeModification);
        Assert.True(library.ModifiedAt <= afterModification);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Manga_TimestampsShouldBeSetCorrectly(TestDatabaseBackend backend)
    {
        await using var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        await using var context = await database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled",
        };
        context.Libraries.Add(library);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };

        // Act
        context.MangaSeries.Add(manga);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(manga.CreatedAt >= beforeCreation);
        Assert.True(manga.CreatedAt <= afterCreation);
        Assert.True(manga.ModifiedAt >= beforeCreation);
        Assert.True(manga.ModifiedAt <= afterCreation);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task MangaAlternativeTitle_CreatedAtShouldBeSet(TestDatabaseBackend backend)
    {
        await using var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        await using var context = await database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled",
        };
        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };
        context.Libraries.Add(library);
        context.MangaSeries.Add(manga);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var alternativeTitle = new MangaAlternativeTitle
        {
            Title = "Alternative Title",
            MangaId = manga.Id,
            Manga = manga,
        };

        // Act
        context.MangaAlternativeTitles.Add(alternativeTitle);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(alternativeTitle.CreatedAt >= beforeCreation);
        Assert.True(alternativeTitle.CreatedAt <= afterCreation);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Chapter_TimestampsShouldBeSetCorrectly(TestDatabaseBackend backend)
    {
        await using var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        await using var context = await database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled",
        };
        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };
        context.Libraries.Add(library);
        context.MangaSeries.Add(manga);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "Test Manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
        };

        // Act
        context.Chapters.Add(chapter);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(chapter.CreatedAt >= beforeCreation);
        Assert.True(chapter.CreatedAt <= afterCreation);
        Assert.True(chapter.ModifiedAt >= beforeCreation);
        Assert.True(chapter.ModifiedAt <= afterCreation);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task UpscalerProfile_TimestampsShouldBeSetCorrectly(TestDatabaseBackend backend)
    {
        await using var database = await _fixture.CreateDatabaseAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        await using var context = await database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Webp,
            Quality = 80,
        };

        context.UpscalerProfiles.Add(profile);
#pragma warning disable xUnit1051
        await context.SaveChangesAsync();
#pragma warning restore xUnit1051
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(profile.CreatedAt >= beforeCreation);
        Assert.True(profile.CreatedAt <= afterCreation);
        Assert.True(profile.ModifiedAt >= beforeCreation);
        Assert.True(profile.ModifiedAt <= afterCreation);
    }
}
