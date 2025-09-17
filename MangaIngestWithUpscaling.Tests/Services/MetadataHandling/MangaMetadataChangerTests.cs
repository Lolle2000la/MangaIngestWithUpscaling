using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using MudBlazor;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.MetadataHandling;

public class MangaMetadataChangerTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly MangaMetadataChanger _metadataChanger;
    private readonly IChapterChangedNotifier _mockChapterChangedNotifier;
    private readonly IMangaChangedNotifier _mockMangaChangedNotifier;
    private readonly IDialogService _mockDialogService;
    private readonly IFileSystem _mockFileSystem;
    private readonly ILogger<MangaMetadataChanger> _mockLogger;
    private readonly IMetadataHandlingService _mockMetadataHandling;
    private readonly ITaskQueue _mockTaskQueue;
    private readonly string _tempDir;
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public MangaMetadataChangerTests()
    {
        // Create SQLite in-memory database
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _dbContext = _testDb.Context;

        // Create mocks
        _mockMetadataHandling = Substitute.For<IMetadataHandlingService>();
        _mockDialogService = Substitute.For<IDialogService>();
        _mockLogger = Substitute.For<ILogger<MangaMetadataChanger>>();
        _mockTaskQueue = Substitute.For<ITaskQueue>();
        _mockFileSystem = Substitute.For<IFileSystem>();
        _mockChapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        _mockMangaChangedNotifier = Substitute.For<IMangaChangedNotifier>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"metadata_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _metadataChanger = new MangaMetadataChanger(
            _mockMetadataHandling,
            _dbContext,
            _mockDialogService,
            _mockLogger,
            _mockTaskQueue,
            _mockFileSystem,
            _mockChapterChangedNotifier,
            _mockMangaChangedNotifier
        );
    }

    public void Dispose()
    {
        _testDb?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithNewTitle_ShouldUpdateTitleAndChapters()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Original Title");
        var chapter = CreateTestChapter(manga, "chapter1.cbz");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chapterPath)!);
        await File.WriteAllTextAsync(chapterPath, "dummy content", TestContext.Current.CancellationToken);

        var newTitle = "New Title";
        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");

        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(chapterPath).Returns(metadata);

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(manga, newTitle, addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RenameResult.Ok, result);
        Assert.Equal(newTitle, manga.PrimaryTitle);

        // Verify old title was added to alternatives
        Assert.Contains(manga.OtherTitles, t => t.Title == "Original Title");

        // Verify metadata was updated
        _mockMetadataHandling.Received(1).WriteComicInfo(chapterPath,
            Arg.Is<ExtractedMetadata>(m => m.Series == newTitle));

        // Verify file operations
        _mockFileSystem.Received().CreateDirectory(Arg.Any<string>());
        _mockFileSystem.Received().Move(Arg.Any<string>(), Arg.Any<string>());

        // Verify notification
        await _mockChapterChangedNotifier.Received(1).Notify(chapter, false);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_ToExistingTitle_ShouldPromptForMerge()
    {
        // Arrange
        var library = CreateTestLibrary();
        var existingManga = CreateTestManga(library, "Existing Title");
        var currentManga = CreateTestManga(library, "Current Title");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddRangeAsync(existingManga, currentManga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Mock dialog service to return true for merge consent
        _mockDialogService.ShowMessageBox(
                "Merge into existing manga of same name?",
                "The title you are trying to rename to already has an existing entry. Do you want to merge this manga into the existing one?",
                "Merge",
                cancelText: "Cancel")
            .Returns(Task.FromResult<bool?>(true));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(currentManga, "Existing Title",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RenameResult.Merged, result);

        // Verify merge task was enqueued
        await _mockTaskQueue.Received(1).EnqueueAsync(
            Arg.Is<MergeMangaTask>(t => t.IntoMangaId == existingManga.Id && t.ToMerge.Contains(currentManga.Id)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_ToExistingTitleWithCancelledMerge_ShouldReturnCancelled()
    {
        // Arrange
        var library = CreateTestLibrary();
        var existingManga = CreateTestManga(library, "Existing Title");
        var currentManga = CreateTestManga(library, "Current Title");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddRangeAsync(existingManga, currentManga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Mock dialog service to return false for merge consent
        _mockDialogService.ShowMessageBox(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                cancelText: Arg.Any<string>())
            .Returns(Task.FromResult<bool?>(false));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(currentManga, "Existing Title",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RenameResult.Cancelled, result);

        // Verify no merge task was enqueued
        await _mockTaskQueue.DidNotReceive().EnqueueAsync(Arg.Any<MergeMangaTask>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeChapterTitle_ShouldUpdateBothNotUpscaledAndUpscaledFiles()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Test Manga");
        var chapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var notUpscaledPath = chapter.NotUpscaledFullPath;
        var upscaledPath = chapter.UpscaledFullPath!;

        Directory.CreateDirectory(Path.GetDirectoryName(notUpscaledPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledPath)!);
        await File.WriteAllTextAsync(notUpscaledPath, "dummy content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(upscaledPath, "dummy upscaled content", TestContext.Current.CancellationToken);

        var metadata = new ExtractedMetadata("Test Manga", "Old Chapter Title", "1");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(Arg.Any<string>()).Returns(metadata);

        var newTitle = "New Chapter Title";

        // Act
        await _metadataChanger.ChangeChapterTitle(chapter, newTitle);

        // Assert
        // Verify both files were updated
        _mockMetadataHandling.Received(1).WriteComicInfo(notUpscaledPath,
            Arg.Is<ExtractedMetadata>(m => m.ChapterTitle == newTitle));
        _mockMetadataHandling.Received(1).WriteComicInfo(upscaledPath,
            Arg.Is<ExtractedMetadata>(m => m.ChapterTitle == newTitle));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeChapterTitle_WithNonUpscaledChapter_ShouldOnlyUpdateOneFile()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Test Manga");
        var chapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: false);

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var notUpscaledPath = chapter.NotUpscaledFullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(notUpscaledPath)!);
        await File.WriteAllTextAsync(notUpscaledPath, "dummy content", TestContext.Current.CancellationToken);

        var metadata = new ExtractedMetadata("Test Manga", "Old Chapter Title", "1");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(notUpscaledPath).Returns(metadata);

        var newTitle = "New Chapter Title";

        // Act
        await _metadataChanger.ChangeChapterTitle(chapter, newTitle);

        // Assert
        // Verify only not-upscaled file was updated
        _mockMetadataHandling.Received(1).WriteComicInfo(notUpscaledPath,
            Arg.Is<ExtractedMetadata>(m => m.ChapterTitle == newTitle));

        // Verify no other WriteComicInfo calls were made
        _mockMetadataHandling.Received(1).WriteComicInfo(Arg.Any<string>(), Arg.Any<ExtractedMetadata>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyMangaTitleToUpscaled_WithMissingFile_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Test Manga");
        var chapter = CreateTestChapter(manga, "chapter1.cbz");

        var nonExistentPath = "nonexistent.cbz";
        var newTitle = "New Title";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            _metadataChanger.ApplyMangaTitleToUpscaled(chapter, newTitle, nonExistentPath));

        Assert.Contains("Chapter file not found", exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyMangaTitleToUpscaled_WithNullManga_ShouldThrowArgumentNullException()
    {
        // Arrange
        var chapter = new Chapter
        {
            Id = 1,
            FileName = "chapter1.cbz",
            RelativePath = "Test Manga/chapter1.cbz",
            Manga = null!, // Null manga
            IsUpscaled = true
        };

        var chapterPath = Path.Combine(_tempDir, "chapter1.cbz");
        File.WriteAllText(chapterPath, "dummy content");

        var newTitle = "New Title";

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _metadataChanger.ApplyMangaTitleToUpscaled(chapter, newTitle, chapterPath));

        Assert.Contains("Chapter manga or library not found", exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithMissingChapterFile_ShouldSkipChapterAndContinue()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Original Title");
        var chapter1 = CreateTestChapter(manga, "chapter1.cbz");
        var chapter2 = CreateTestChapter(manga, "chapter2.cbz");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddRangeAsync(chapter1, chapter2);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Only create file for chapter2, chapter1 is missing
        var chapter2Path = Path.Combine(library.NotUpscaledLibraryPath, chapter2.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chapter2Path)!);
        await File.WriteAllTextAsync(chapter2Path, "dummy content", TestContext.Current.CancellationToken);

        var metadata = new ExtractedMetadata("Original Title", "Chapter 2", "2");
        _mockMetadataHandling.GetSeriesAndTitleFromComicInfo(chapter2Path).Returns(metadata);

        var newTitle = "New Title";

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(manga, newTitle,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RenameResult.Ok, result);
        Assert.Equal(newTitle, manga.PrimaryTitle);

        // Verify warning was logged for missing chapter1
        _mockLogger.ReceivedWithAnyArgs().LogWarning(default!);

        // Verify chapter2 was processed
        _mockMetadataHandling.Received(1).WriteComicInfo(chapter2Path,
            Arg.Is<ExtractedMetadata>(m => m.Series == newTitle));
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