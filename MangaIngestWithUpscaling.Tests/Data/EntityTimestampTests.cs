using Microsoft.EntityFrameworkCore;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Tests.Data;

public class EntityTimestampTests : IDisposable
{
    private readonly ApplicationDbContext _context;

    public EntityTimestampTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    [Fact]
    public async Task Library_CreatedAt_ShouldBeSetOnCreation()
    {
        // Arrange
        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled"
        };

        // Act
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(library.CreatedAt >= beforeCreation);
        Assert.True(library.CreatedAt <= afterCreation);
        Assert.True(library.ModifiedAt >= beforeCreation);
        Assert.True(library.ModifiedAt <= afterCreation);
    }

    [Fact]
    public async Task Library_ModifiedAt_ShouldBeUpdatedOnModification()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled"
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        var originalModifiedAt = library.ModifiedAt;
        
        // Wait a moment to ensure time difference
        await Task.Delay(100);
        var beforeModification = DateTime.UtcNow;

        // Act
        library.Name = "Modified Library";
        await _context.SaveChangesAsync();
        var afterModification = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(library.ModifiedAt > originalModifiedAt);
        Assert.True(library.ModifiedAt >= beforeModification);
        Assert.True(library.ModifiedAt <= afterModification);
    }

    [Fact]
    public async Task Manga_TimestampsShouldBeSetCorrectly()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled"
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library
        };

        // Act
        _context.MangaSeries.Add(manga);
        await _context.SaveChangesAsync();
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(manga.CreatedAt >= beforeCreation);
        Assert.True(manga.CreatedAt <= afterCreation);
        Assert.True(manga.ModifiedAt >= beforeCreation);
        Assert.True(manga.ModifiedAt <= afterCreation);
    }

    [Fact]
    public async Task MangaAlternativeTitle_CreatedAtShouldBeSet()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled"
        };
        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            Library = library
        };
        _context.Libraries.Add(library);
        _context.MangaSeries.Add(manga);
        await _context.SaveChangesAsync();

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var alternativeTitle = new MangaAlternativeTitle
        {
            Title = "Alternative Title",
            MangaId = manga.Id,
            Manga = manga
        };

        // Act
        _context.MangaAlternativeTitles.Add(alternativeTitle);
        await _context.SaveChangesAsync();
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(alternativeTitle.CreatedAt >= beforeCreation);
        Assert.True(alternativeTitle.CreatedAt <= afterCreation);
    }

    [Fact]
    public async Task Chapter_TimestampsShouldBeSetCorrectly()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/notupscaled"
        };
        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            Library = library
        };
        _context.Libraries.Add(library);
        _context.MangaSeries.Add(manga);
        await _context.SaveChangesAsync();

        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "Test Manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga
        };

        // Act
        _context.Chapters.Add(chapter);
        await _context.SaveChangesAsync();
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(chapter.CreatedAt >= beforeCreation);
        Assert.True(chapter.CreatedAt <= afterCreation);
        Assert.True(chapter.ModifiedAt >= beforeCreation);
        Assert.True(chapter.ModifiedAt <= afterCreation);
    }

    [Fact]
    public async Task UpscalerProfile_TimestampsShouldBeSetCorrectly()
    {
        // Arrange
        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Webp,
            Quality = 80
        };

        // Act
        _context.UpscalerProfiles.Add(profile);
        await _context.SaveChangesAsync();
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(profile.CreatedAt >= beforeCreation);
        Assert.True(profile.CreatedAt <= afterCreation);
        Assert.True(profile.ModifiedAt >= beforeCreation);
        Assert.True(profile.ModifiedAt <= afterCreation);
    }
}