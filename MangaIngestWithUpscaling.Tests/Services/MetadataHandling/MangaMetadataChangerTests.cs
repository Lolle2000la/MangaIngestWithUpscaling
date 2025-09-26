using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
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

        _tempDir = Path.Combine(Path.GetTempPath(), $"metadata_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _metadataChanger = new MangaMetadataChanger(
            _mockMetadataHandling,
            _dbContext,
            _mockDialogService,
            _mockLogger,
            _mockTaskQueue,
            _mockFileSystem,
            _mockChapterChangedNotifier
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
        await File.WriteAllTextAsync(
            chapterPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        var newTitle = "New Title";
        var newChapterPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );
        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");

        // Set up mocks - source file exists, target doesn't
        _mockFileSystem.FileExists(chapterPath).Returns(true);
        _mockFileSystem.FileExists(newChapterPath).Returns(false);
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(chapterPath)
            .Returns(Task.FromResult(metadata));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Ok, result);
        Assert.Equal(newTitle, manga.PrimaryTitle);

        // Verify old title was added to alternatives
        Assert.Contains(manga.OtherTitles, t => t.Title == "Original Title");

        // Verify metadata was updated
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(chapterPath,
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
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
        _mockDialogService
            .ShowMessageBox(
                "Merge into existing manga of same name?",
                "The title you are trying to rename to already has an existing entry. Do you want to merge this manga into the existing one?",
                "Merge",
                cancelText: "Cancel"
            )
            .Returns(Task.FromResult<bool?>(true));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            currentManga,
            "Existing Title",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Merged, result);

        // Verify merge task was enqueued
        await _mockTaskQueue
            .Received(1)
            .EnqueueAsync(
                Arg.Is<MergeMangaTask>(t =>
                    t.IntoMangaId == existingManga.Id && t.ToMerge.Contains(currentManga.Id)
                )
            );
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
        _mockDialogService
            .ShowMessageBox(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                cancelText: Arg.Any<string>()
            )
            .Returns(Task.FromResult<bool?>(false));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            currentManga,
            "Existing Title",
            cancellationToken: TestContext.Current.CancellationToken
        );

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
        await File.WriteAllTextAsync(
            notUpscaledPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            upscaledPath,
            "dummy upscaled content",
            TestContext.Current.CancellationToken
        );

        var metadata = new ExtractedMetadata("Test Manga", "Old Chapter Title", "1");
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(Task.FromResult(metadata));

        var newTitle = "New Chapter Title";

        // Act
        await _metadataChanger.ChangeChapterTitle(chapter, newTitle);

        // Assert
        // Verify both files were updated
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(
                notUpscaledPath,
                Arg.Is<ExtractedMetadata>(m => m.ChapterTitle == newTitle)
            );
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(
                upscaledPath,
                Arg.Is<ExtractedMetadata>(m => m.ChapterTitle == newTitle)
            );
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
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
        await File.WriteAllTextAsync(
            notUpscaledPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        var metadata = new ExtractedMetadata("Test Manga", "Old Chapter Title", "1");
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(notUpscaledPath)
            .Returns(Task.FromResult(metadata));

        var newTitle = "New Chapter Title";

        // Act
        await _metadataChanger.ChangeChapterTitle(chapter, newTitle);

        // Assert
        // Verify only not-upscaled file was updated
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(
                notUpscaledPath,
                Arg.Is<ExtractedMetadata>(m => m.ChapterTitle == newTitle)
            );

        // Verify no other WriteComicInfo calls were made
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(Arg.Any<string>(), Arg.Any<ExtractedMetadata>());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyMangaTitleToUpscaled_WithMissingFile_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Test Manga");
        var chapter = CreateTestChapter(manga, "chapter1.cbz");

        var nonExistentPath = "nonexistent.cbz";
        var newTitle = "New Title";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _metadataChanger.ApplyMangaTitleToUpscaledAsync(chapter, newTitle, nonExistentPath)
        );

        Assert.Contains("Chapter file not found", exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyMangaTitleToUpscaled_WithNullManga_ShouldThrowArgumentNullException()
    {
        // Arrange
        var chapter = new Chapter
        {
            Id = 1,
            FileName = "chapter1.cbz",
            RelativePath = "Test Manga/chapter1.cbz",
            Manga = null!, // Null manga
            IsUpscaled = true,
        };

        var chapterPath = Path.Combine(_tempDir, "chapter1.cbz");
        File.WriteAllText(chapterPath, "dummy content");

        var newTitle = "New Title";

        _mockFileSystem.FileExists(chapterPath).Returns(true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _metadataChanger.ApplyMangaTitleToUpscaledAsync(chapter, newTitle, chapterPath)
        );

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
        var chapter1Path = Path.Combine(library.NotUpscaledLibraryPath, chapter1.RelativePath);
        var chapter2Path = Path.Combine(library.NotUpscaledLibraryPath, chapter2.RelativePath);
        var newTitle = "New Title";
        var newChapter2Path = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter2.FileName)
        );

        Directory.CreateDirectory(Path.GetDirectoryName(chapter2Path)!);
        await File.WriteAllTextAsync(
            chapter2Path,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        var metadata = new ExtractedMetadata("Original Title", "Chapter 2", "2");

        // Set up mocks - chapter1 missing, chapter2 exists, no target conflicts
        _mockFileSystem.FileExists(chapter1Path).Returns(false);
        _mockFileSystem.FileExists(chapter2Path).Returns(true);
        _mockFileSystem.FileExists(newChapter2Path).Returns(false);
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(chapter2Path)
            .Returns(Task.FromResult(metadata));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Ok, result);
        Assert.Equal(newTitle, manga.PrimaryTitle);

        // Verify warning was logged for missing chapter1
        _mockLogger.ReceivedWithAnyArgs().LogWarning(default!);

        // Verify chapter2 was processed
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(
                chapter2Path,
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Arg.Is<ExtractedMetadata>(m => m.Series == newTitle)
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithFileConflict_ShouldCancelEarly()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Original Title");
        var chapter = CreateTestChapter(manga, "chapter1.cbz");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create source file
        var chapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chapterPath)!);
        await File.WriteAllTextAsync(
            chapterPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        // Create conflicting target file
        var newTitle = "New Title";
        var targetPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(
            targetPath,
            "existing content",
            TestContext.Current.CancellationToken
        );

        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");

        // Set up mocks - both source and target files exist (conflict)
        _mockFileSystem.FileExists(chapterPath).Returns(true);
        _mockFileSystem.FileExists(targetPath).Returns(true); // This should cause conflict
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(chapterPath)
            .Returns(Task.FromResult(metadata));

        var originalTitle = manga.PrimaryTitle;

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Cancelled, result);

        // Verify no database changes were made
        Assert.Equal(originalTitle, manga.PrimaryTitle);
        Assert.DoesNotContain(manga.OtherTitles, t => t.Title == originalTitle);

        // Verify no file operations were performed
        _mockFileSystem.DidNotReceive().CreateDirectory(Arg.Any<string>());
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());

        // Verify warning was logged about file conflict
        _mockLogger
            .Received()
            .Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("already exists")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithMissingSourceFile_ShouldSkipFileAndContinue()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Original Title");
        var chapter = CreateTestChapter(manga, "chapter1.cbz");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Don't create the source file - it's missing
        var chapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);

        // Set up mocks - source file missing
        _mockFileSystem.FileExists(chapterPath).Returns(false);

        var newTitle = "New Title";
        var originalTitle = manga.PrimaryTitle;

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Ok, result);

        // Verify title was still updated (operation succeeds with warning)
        Assert.Equal(newTitle, manga.PrimaryTitle);
        Assert.Contains(manga.OtherTitles, t => t.Title == originalTitle);

        // Verify warning was logged for missing file
        _mockLogger
            .Received()
            .Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("not found")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>()
            );

        // Verify no file operations were performed due to missing source
        _mockFileSystem.DidNotReceive().CreateDirectory(Arg.Any<string>());
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithValidOperation_ShouldPerformFileSystemActions()
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
        var newTitle = "New Title";
        var newChapterPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );

        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");

        // Set up mocks - source file exists, target doesn't
        _mockFileSystem.FileExists(chapterPath).Returns(true);
        _mockFileSystem.FileExists(newChapterPath).Returns(false);
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(chapterPath)
            .Returns(Task.FromResult(metadata));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Ok, result);

        // Verify file system operations were performed
        _mockFileSystem.Received(1).CreateDirectory(Arg.Any<string>());
        _mockFileSystem.Received(1).Move(chapterPath, newChapterPath);

        // Verify metadata was updated
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(chapterPath,
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Arg.Is<ExtractedMetadata>(m => m.Series == newTitle));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithTargetFileExists_ShouldNotPerformFileOperations()
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
        var newTitle = "New Title";
        var newChapterPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );

        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");
        var originalTitle = manga.PrimaryTitle;

        // Set up mocks - both source and target files exist (conflict)
        _mockFileSystem.FileExists(chapterPath).Returns(true);
        _mockFileSystem.FileExists(newChapterPath).Returns(true); // This should cause conflict
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(chapterPath)
            .Returns(Task.FromResult(metadata));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Cancelled, result);

        // Verify no database changes were made
        Assert.Equal(originalTitle, manga.PrimaryTitle);
        Assert.DoesNotContain(manga.OtherTitles, t => t.Title == originalTitle);

        // Verify NO file operations were performed due to conflict detection
        _mockFileSystem.DidNotReceive().CreateDirectory(Arg.Any<string>());
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());

        // Verify NO metadata updates were attempted
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .DidNotReceive()
            .WriteComicInfoAsync(Arg.Any<string>(), Arg.Any<ExtractedMetadata>());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithUpscaledChapter_ShouldHandleBothFiles()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Original Title");
        var chapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        var upscaledPath = Path.Combine(library.UpscaledLibraryPath!, chapter.RelativePath);
        var newTitle = "New Title";
        var newChapterPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );
        var newUpscaledPath = Path.Combine(
            library.UpscaledLibraryPath!,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );

        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");

        // Set up mocks - all source files exist, no target conflicts
        _mockFileSystem.FileExists(chapterPath).Returns(true);
        _mockFileSystem.FileExists(upscaledPath).Returns(true);
        _mockFileSystem.FileExists(newChapterPath).Returns(false);
        _mockFileSystem.FileExists(newUpscaledPath).Returns(false);
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(Task.FromResult(metadata));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Ok, result);

        // Verify file system operations were performed for both files
        _mockFileSystem.Received(2).CreateDirectory(Arg.Any<string>()); // Once for each file
        _mockFileSystem.Received(1).Move(chapterPath, newChapterPath);
        _mockFileSystem.Received(1).Move(upscaledPath, newUpscaledPath);

        // Verify metadata was updated for both files
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(chapterPath, Arg.Is<ExtractedMetadata>(m => m.Series == newTitle));
        _mockMetadataHandling
            .Received(1)
            .WriteComicInfoAsync(
                upscaledPath,
                Arg.Is<ExtractedMetadata>(m => m.Series == newTitle)
            );
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangeMangaTitle_WithUpscaledFileConflict_ShouldNotPerformAnyFileOperations()
    {
        // Arrange
        var library = CreateTestLibrary();
        var manga = CreateTestManga(library, "Original Title");
        var chapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(chapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        var upscaledPath = Path.Combine(library.UpscaledLibraryPath!, chapter.RelativePath);
        var newTitle = "New Title";
        var newUpscaledPath = Path.Combine(
            library.UpscaledLibraryPath!,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName)
        );

        var metadata = new ExtractedMetadata("Original Title", "Chapter 1", "1");
        var originalTitle = manga.PrimaryTitle;

        // Set up mocks - source files exist, but upscaled target conflicts
        _mockFileSystem.FileExists(chapterPath).Returns(true);
        _mockFileSystem.FileExists(upscaledPath).Returns(true);
        _mockFileSystem.FileExists(newUpscaledPath).Returns(true); // Conflict in upscaled file
        _mockMetadataHandling
            .GetSeriesAndTitleFromComicInfoAsync(chapterPath)
            .Returns(Task.FromResult(metadata));

        // Act
        var result = await _metadataChanger.ChangeMangaTitle(
            manga,
            newTitle,
            addOldToAlternative: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(RenameResult.Cancelled, result);

        // Verify no database changes were made
        Assert.Equal(originalTitle, manga.PrimaryTitle);

        // Verify NO file operations were performed due to conflict detection
        _mockFileSystem.DidNotReceive().CreateDirectory(Arg.Any<string>());
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());

        // Verify NO metadata updates were attempted
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        _mockMetadataHandling
            .DidNotReceive()
            .WriteComicInfoAsync(Arg.Any<string>(), Arg.Any<ExtractedMetadata>());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
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
            UpscaledLibraryPath = upscaledPath,
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
            OtherTitles = new List<MangaAlternativeTitle>(),
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
            IsUpscaled = isUpscaled,
        };
    }
}
