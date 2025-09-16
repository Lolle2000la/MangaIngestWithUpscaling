using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MangaManagement;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.MangaManagement;

public class MangaMergerTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IMetadataHandlingService _mockMetadataHandling;
    private readonly IMangaMetadataChanger _mockMetadataChanger;
    private readonly ILogger<MangaMerger> _mockLogger;
    private readonly IFileSystem _mockFileSystem;
    private readonly IChapterChangedNotifier _mockChapterChangedNotifier;
    private readonly MangaMerger _mangaMerger;
    private readonly string _tempDir;

    public MangaMergerTests()
    {
        // Create in-memory database
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        // Create mocks
        _mockMetadataHandling = Substitute.For<IMetadataHandlingService>();
        _mockMetadataChanger = Substitute.For<IMangaMetadataChanger>();
        _mockLogger = Substitute.For<ILogger<MangaMerger>>();
        _mockFileSystem = Substitute.For<IFileSystem>();
        _mockChapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"merger_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _mangaMerger = new MangaMerger(
            _dbContext,
            _mockMetadataHandling,
            _mockMetadataChanger,
            _mockLogger,
            _mockFileSystem,
            _mockChapterChangedNotifier
        );
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithValidData_ShouldMergeSuccessfully()
    {
        // Arrange
        var library = CreateTestLibrary();
        var primaryManga = CreateTestManga(library, "Primary Manga");
        var mergedManga = CreateTestManga(library, "Merged Manga");
        var chapter1 = CreateTestChapter(mergedManga, "chapter1.cbz");
        var chapter2 = CreateTestChapter(mergedManga, "chapter2.cbz");

        await _dbContext.Libraries.AddAsync(library);
        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.Chapters.AddRangeAsync(chapter1, chapter2);
        await _dbContext.SaveChangesAsync();

        // Create test files
        var chapter1Path = Path.Combine(library.NotUpscaledLibraryPath, chapter1.RelativePath);
        var chapter2Path = Path.Combine(library.NotUpscaledLibraryPath, chapter2.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chapter1Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(chapter2Path)!);
        await File.WriteAllTextAsync(chapter1Path, "chapter1 content");
        await File.WriteAllTextAsync(chapter2Path, "chapter2 content");

        var metadata1 = new ExtractedMetadata("Merged Manga", "Chapter 1", "1");
        var metadata2 = new ExtractedMetadata("Merged Manga", "Chapter 2", "2");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(chapter1Path).Returns(metadata1);
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(chapter2Path).Returns(metadata2);

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify chapters were transferred to primary manga
        Assert.Equal(2, primaryManga.Chapters.Count);
        Assert.Contains(chapter1, primaryManga.Chapters);
        Assert.Contains(chapter2, primaryManga.Chapters);
        
        // Verify merged manga was removed
        Assert.DoesNotContain(mergedManga, _dbContext.MangaSeries);
        
        // Verify alternative title was added
        Assert.Contains(primaryManga.OtherTitles, t => t.Title == "Merged Manga");
        
        // Verify file operations
        _mockFileSystem.Received(2).Move(Arg.Any<string>(), Arg.Any<string>());
        
        // Verify metadata updates
        _mockMetadataHandling.Received(2).WriteComicInfo(
            Arg.Any<string>(),
            Arg.Is<ExtractedMetadata>(m => m.Series == "Primary Manga"));
        
        // Verify notifications
        await _mockChapterChangedNotifier.Received(2).Notify(Arg.Any<Chapter>(), false);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithUpscaledChapters_ShouldHandleUpscaledFiles()
    {
        // Arrange
        var library = CreateTestLibrary();
        var primaryManga = CreateTestManga(library, "Primary Manga");
        var mergedManga = CreateTestManga(library, "Merged Manga");
        var chapter = CreateTestChapter(mergedManga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddAsync(library);
        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.Chapters.AddAsync(chapter);
        await _dbContext.SaveChangesAsync();

        // Create test files
        var chapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        var upscaledChapterPath = Path.Combine(library.UpscaledLibraryPath!, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chapterPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledChapterPath)!);
        await File.WriteAllTextAsync(chapterPath, "chapter content");
        await File.WriteAllTextAsync(upscaledChapterPath, "upscaled chapter content");

        var metadata = new ExtractedMetadata("Merged Manga", "Chapter 1", "1");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(chapterPath).Returns(metadata);

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify chapter was transferred
        Assert.Contains(chapter, primaryManga.Chapters);
        
        // Verify upscaled file was processed
        _mockMetadataChanger.Received(1).ApplyMangaTitleToUpscaled(
            chapter, "Primary Manga", upscaledChapterPath);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithMissingFiles_ShouldSkipMissingAndContinue()
    {
        // Arrange
        var library = CreateTestLibrary();
        var primaryManga = CreateTestManga(library, "Primary Manga");
        var mergedManga = CreateTestManga(library, "Merged Manga");
        var chapter1 = CreateTestChapter(mergedManga, "chapter1.cbz"); // Missing file
        var chapter2 = CreateTestChapter(mergedManga, "chapter2.cbz"); // Exists

        await _dbContext.Libraries.AddAsync(library);
        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.Chapters.AddRangeAsync(chapter1, chapter2);
        await _dbContext.SaveChangesAsync();

        // Only create file for chapter2
        var chapter2Path = Path.Combine(library.NotUpscaledLibraryPath, chapter2.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chapter2Path)!);
        await File.WriteAllTextAsync(chapter2Path, "chapter2 content");

        var metadata2 = new ExtractedMetadata("Merged Manga", "Chapter 2", "2");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(chapter2Path).Returns(metadata2);

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify warning was logged for missing chapter
        _mockLogger.Received().LogWarning(
            Arg.Is<string>(s => s.Contains("does not exist")),
            Arg.Any<object[]>());
        
        // Verify merged manga was NOT removed (since not all chapters could be moved)
        Assert.Contains(mergedManga, _dbContext.MangaSeries);
        
        // Verify no chapters were transferred (since not all could be moved)
        Assert.Empty(primaryManga.Chapters);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithExistingTargetFiles_ShouldSkipConflicting()
    {
        // Arrange
        var library = CreateTestLibrary();
        var primaryManga = CreateTestManga(library, "Primary Manga");
        var mergedManga = CreateTestManga(library, "Merged Manga");
        var chapter = CreateTestChapter(mergedManga, "chapter1.cbz");

        await _dbContext.Libraries.AddAsync(library);
        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.Chapters.AddAsync(chapter);
        await _dbContext.SaveChangesAsync();

        // Create source file
        var sourcePath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "chapter content");

        // Create conflicting target file
        var targetPath = Path.Combine(library.NotUpscaledLibraryPath, "Primary Manga", "chapter1.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "existing content");

        var metadata = new ExtractedMetadata("Merged Manga", "Chapter 1", "1");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(sourcePath).Returns(metadata);

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify warning was logged for conflicting file
        _mockLogger.Received().LogWarning(
            Arg.Is<string>(s => s.Contains("already exists")),
            Arg.Any<object[]>());
        
        // Verify merged manga was NOT removed
        Assert.Contains(mergedManga, _dbContext.MangaSeries);
        
        // Verify no file moves occurred
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithNoChaptersToMove_ShouldTransferTitlesOnly()
    {
        // Arrange
        var library = CreateTestLibrary();
        var primaryManga = CreateTestManga(library, "Primary Manga");
        var mergedManga = CreateTestManga(library, "Merged Manga");
        
        // Add alternative title to merged manga
        mergedManga.OtherTitles.Add(new MangaAlternativeTitle 
        { 
            Title = "Alternative Title", 
            Manga = mergedManga, 
            MangaId = mergedManga.Id 
        });

        await _dbContext.Libraries.AddAsync(library);
        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.SaveChangesAsync();

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify merged manga was removed
        Assert.DoesNotContain(mergedManga, _dbContext.MangaSeries);
        
        // Verify titles were transferred
        Assert.Contains(primaryManga.OtherTitles, t => t.Title == "Merged Manga");
        Assert.Contains(primaryManga.OtherTitles, t => t.Title == "Alternative Title");
        
        // Verify no file operations occurred
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithDuplicateTitles_ShouldAvoidDuplicates()
    {
        // Arrange
        var library = CreateTestLibrary();
        var primaryManga = CreateTestManga(library, "Primary Manga");
        var mergedManga = CreateTestManga(library, "Primary Manga"); // Same title as primary
        
        // Add existing alternative title to primary manga
        primaryManga.OtherTitles.Add(new MangaAlternativeTitle 
        { 
            Title = "Existing Title", 
            Manga = primaryManga, 
            MangaId = primaryManga.Id 
        });
        
        // Add same alternative title to merged manga (should not be duplicated)
        mergedManga.OtherTitles.Add(new MangaAlternativeTitle 
        { 
            Title = "Existing Title", 
            Manga = mergedManga, 
            MangaId = mergedManga.Id 
        });

        await _dbContext.Libraries.AddAsync(library);
        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.SaveChangesAsync();

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify merged manga was removed
        Assert.DoesNotContain(mergedManga, _dbContext.MangaSeries);
        
        // Verify no duplicate titles were added
        var existingTitleCount = primaryManga.OtherTitles.Count(t => t.Title == "Existing Title");
        Assert.Equal(1, existingTitleCount);
        
        // Verify primary title wasn't added as alternative (same as primary)
        Assert.DoesNotContain(primaryManga.OtherTitles, t => t.Title == "Primary Manga");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithNullLibrary_ShouldLogErrorAndReturn()
    {
        // Arrange
        var primaryManga = new Manga
        {
            Id = 1,
            PrimaryTitle = "Primary Manga",
            Library = null!, // Null library
            OtherTitles = new List<MangaAlternativeTitle>(),
            Chapters = new List<Chapter>()
        };
        
        var mergedManga = CreateTestManga(CreateTestLibrary(), "Merged Manga");

        await _dbContext.MangaSeries.AddRangeAsync(primaryManga, mergedManga);
        await _dbContext.SaveChangesAsync();

        // Act
        await _mangaMerger.MergeAsync(primaryManga, [mergedManga]);

        // Assert
        // Verify error was logged
        _mockLogger.Received().LogError(
            Arg.Is<string>(s => s.Contains("must have an associated library")),
            Arg.Any<object[]>());
        
        // Verify no operations occurred
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());
    }

    private Library CreateTestLibrary()
    {
        var notUpscaledPath = Path.Combine(_tempDir, "not_upscaled");
        var upscaledPath = Path.Combine(_tempDir, "upscaled");
        
        Directory.CreateDirectory(notUpscaledPath);
        Directory.CreateDirectory(upscaledPath);
        
        return new Library
        {
            Id = 1,
            Name = "Test Library",
            NotUpscaledLibraryPath = notUpscaledPath,
            UpscaledLibraryPath = upscaledPath
        };
    }

    private Manga CreateTestManga(Library library, string title)
    {
        return new Manga
        {
            Id = Random.Shared.Next(1, 10000),
            PrimaryTitle = title,
            Library = library,
            LibraryId = library.Id,
            Chapters = new List<Chapter>(),
            OtherTitles = new List<MangaAlternativeTitle>()
        };
    }

    private Chapter CreateTestChapter(Manga manga, string fileName, bool isUpscaled = false)
    {
        var relativePath = Path.Combine(manga.PrimaryTitle, fileName);
        return new Chapter
        {
            Id = Random.Shared.Next(1, 10000),
            FileName = fileName,
            RelativePath = relativePath,
            Manga = manga,
            MangaId = manga.Id,
            IsUpscaled = isUpscaled
        };
    }
}