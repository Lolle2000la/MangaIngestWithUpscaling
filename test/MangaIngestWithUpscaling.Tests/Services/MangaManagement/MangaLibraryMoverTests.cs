using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.MangaManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.MangaManagement;

public class MangaLibraryMoverTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly MangaLibraryMover _libraryMover;
    private readonly IFileSystem _mockFileSystem;
    private readonly ILogger<MangaLibraryMover> _mockLogger;
    private readonly ITaskQueue _mockTaskQueue;
    private readonly string _tempDir;
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public MangaLibraryMoverTests()
    {
        // Create SQLite in-memory database
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _dbContext = _testDb.Context;

        // Create mocks
        _mockLogger = Substitute.For<ILogger<MangaLibraryMover>>();
        _mockTaskQueue = Substitute.For<ITaskQueue>();
        _mockFileSystem = Substitute.For<IFileSystem>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"library_mover_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _libraryMover = new MangaLibraryMover(
            _mockLogger,
            _dbContext,
            _mockTaskQueue,
            _mockFileSystem
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
    public async Task MoveMangaAsync_WithSameLibrary_ShouldReturnEarly()
    {
        // Arrange
        var library = CreateTestLibrary("Source Library");
        var manga = CreateTestManga(library, "Test Manga");

        await _dbContext.Libraries.AddAsync(library, TestContext.Current.CancellationToken);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _libraryMover.MoveMangaAsync(manga, library, TestContext.Current.CancellationToken);

        // Assert
        // Verify no file operations occurred
        _mockFileSystem.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>());
        _mockFileSystem.DidNotReceive().CreateDirectory(Arg.Any<string>());

        // Verify no tasks were enqueued
        await _mockTaskQueue
            .DidNotReceive()
            .EnqueueAsync(Arg.Any<RenameUpscaledChaptersSeriesTask>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveMangaAsync_WithValidMove_ShouldMoveChaptersAndUpdateDatabase()
    {
        // Arrange
        var sourceLibrary = CreateTestLibrary("Source Library");
        var targetLibrary = CreateTestLibrary("Target Library");
        var manga = CreateTestManga(sourceLibrary, "Test Manga");
        var chapter1 = CreateTestChapter(manga, "chapter1.cbz");
        var chapter2 = CreateTestChapter(manga, "chapter2.cbz");

        await _dbContext.Libraries.AddRangeAsync(sourceLibrary, targetLibrary);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddRangeAsync(chapter1, chapter2);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create test files
        var sourcePath1 = Path.Combine(sourceLibrary.NotUpscaledLibraryPath, chapter1.RelativePath);
        var sourcePath2 = Path.Combine(sourceLibrary.NotUpscaledLibraryPath, chapter2.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath2)!);
        await File.WriteAllTextAsync(
            sourcePath1,
            "chapter1 content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            sourcePath2,
            "chapter2 content",
            TestContext.Current.CancellationToken
        );

        // Act
        await _libraryMover.MoveMangaAsync(
            manga,
            targetLibrary,
            TestContext.Current.CancellationToken
        );

        // Assert
        // Verify database was updated
        Assert.Equal(targetLibrary.Id, manga.LibraryId);
        Assert.Equal(targetLibrary, manga.Library);

        // Verify target directories were created
        _mockFileSystem
            .Received(1)
            .CreateDirectory(Path.Combine(targetLibrary.NotUpscaledLibraryPath, "Test Manga"));
        _mockFileSystem
            .Received(1)
            .CreateDirectory(Path.Combine(targetLibrary.UpscaledLibraryPath!, "Test Manga"));

        // Verify files were moved
        _mockFileSystem.Received(2).Move(Arg.Any<string>(), Arg.Any<string>());

        // Verify chapter relative paths were updated
        foreach (var chapter in manga.Chapters)
        {
            Assert.StartsWith("Test Manga", chapter.RelativePath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveMangaAsync_WithUpscaledChapters_ShouldEnqueueRenameTasks()
    {
        // Arrange
        var sourceLibrary = CreateTestLibrary("Source Library");
        var targetLibrary = CreateTestLibrary("Target Library");
        var manga = CreateTestManga(sourceLibrary, "Test Manga");
        var upscaledChapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);
        var regularChapter = CreateTestChapter(manga, "chapter2.cbz", isUpscaled: false);

        await _dbContext.Libraries.AddRangeAsync(sourceLibrary, targetLibrary);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddRangeAsync(upscaledChapter, regularChapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create test files
        var sourcePath1 = Path.Combine(
            sourceLibrary.NotUpscaledLibraryPath,
            upscaledChapter.RelativePath
        );
        var sourcePath2 = Path.Combine(
            sourceLibrary.NotUpscaledLibraryPath,
            regularChapter.RelativePath
        );
        var upscaledPath = Path.Combine(
            sourceLibrary.UpscaledLibraryPath!,
            upscaledChapter.RelativePath
        );

        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath2)!);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledPath)!);

        await File.WriteAllTextAsync(
            sourcePath1,
            "chapter1 content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            sourcePath2,
            "chapter2 content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            upscaledPath,
            "upscaled chapter1 content",
            TestContext.Current.CancellationToken
        );

        // Act
        await _libraryMover.MoveMangaAsync(
            manga,
            targetLibrary,
            TestContext.Current.CancellationToken
        );

        // Assert
        // Verify rename task was enqueued for upscaled chapter only
        await _mockTaskQueue
            .Received(1)
            .EnqueueAsync(
                Arg.Is<RenameUpscaledChaptersSeriesTask>(t =>
                    t.ChapterId == upscaledChapter.Id && t.NewTitle == "Test Manga"
                )
            );

        // Verify only one task was enqueued (not for regular chapter)
        await _mockTaskQueue.Received(1).EnqueueAsync(Arg.Any<RenameUpscaledChaptersSeriesTask>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveMangaAsync_WithMissingUpscaledFile_ShouldSkipAndLogWarning()
    {
        // Arrange
        var sourceLibrary = CreateTestLibrary("Source Library");
        var targetLibrary = CreateTestLibrary("Target Library");
        var manga = CreateTestManga(sourceLibrary, "Test Manga");
        var upscaledChapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddRangeAsync(sourceLibrary, targetLibrary);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(upscaledChapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create only the regular file, not the upscaled one
        var sourcePath = Path.Combine(
            sourceLibrary.NotUpscaledLibraryPath,
            upscaledChapter.RelativePath
        );
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(
            sourcePath,
            "chapter1 content",
            TestContext.Current.CancellationToken
        );
        // Upscaled file intentionally missing

        // Act
        await _libraryMover.MoveMangaAsync(
            manga,
            targetLibrary,
            TestContext.Current.CancellationToken
        );

        // Assert
        // Verify warning was logged for missing upscaled file
        _mockLogger.ReceivedWithAnyArgs().LogWarning(default!);

        // Verify no rename task was enqueued
        await _mockTaskQueue
            .DidNotReceive()
            .EnqueueAsync(Arg.Any<RenameUpscaledChaptersSeriesTask>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveMangaAsync_WithTargetLibraryWithoutUpscaledPath_ShouldSkipUpscaledProcessing()
    {
        // Arrange
        var sourceLibrary = CreateTestLibrary("Source Library");
        var targetLibrary = CreateTestLibrary("Target Library", hasUpscaledPath: false);
        var manga = CreateTestManga(sourceLibrary, "Test Manga");
        var upscaledChapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddRangeAsync(sourceLibrary, targetLibrary);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(upscaledChapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create test files
        var sourcePath = Path.Combine(
            sourceLibrary.NotUpscaledLibraryPath,
            upscaledChapter.RelativePath
        );
        var upscaledPath = Path.Combine(
            sourceLibrary.UpscaledLibraryPath!,
            upscaledChapter.RelativePath
        );

        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledPath)!);

        await File.WriteAllTextAsync(
            sourcePath,
            "chapter1 content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            upscaledPath,
            "upscaled chapter1 content",
            TestContext.Current.CancellationToken
        );

        // Act
        await _libraryMover.MoveMangaAsync(
            manga,
            targetLibrary,
            TestContext.Current.CancellationToken
        );

        // Assert
        // Verify warning was logged for missing target upscaled library path
        _mockLogger.ReceivedWithAnyArgs().LogWarning(default!);

        // Verify no rename task was enqueued
        await _mockTaskQueue
            .DidNotReceive()
            .EnqueueAsync(Arg.Any<RenameUpscaledChaptersSeriesTask>());

        // Verify regular chapter was still moved
        _mockFileSystem.Received(1).Move(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveMangaAsync_WithFileOperationFailure_ShouldLogErrorAndContinue()
    {
        // Arrange
        var sourceLibrary = CreateTestLibrary("Source Library");
        var targetLibrary = CreateTestLibrary("Target Library");
        var manga = CreateTestManga(sourceLibrary, "Test Manga");
        var chapter1 = CreateTestChapter(manga, "chapter1.cbz");
        var chapter2 = CreateTestChapter(manga, "chapter2.cbz");

        await _dbContext.Libraries.AddRangeAsync(sourceLibrary, targetLibrary);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddRangeAsync(chapter1, chapter2);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create test files
        var sourcePath1 = Path.Combine(sourceLibrary.NotUpscaledLibraryPath, chapter1.RelativePath);
        var sourcePath2 = Path.Combine(sourceLibrary.NotUpscaledLibraryPath, chapter2.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath2)!);
        await File.WriteAllTextAsync(
            sourcePath1,
            "chapter1 content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            sourcePath2,
            "chapter2 content",
            TestContext.Current.CancellationToken
        );

        // Mock file system to throw on first move but succeed on second
        _mockFileSystem
            .When(x => x.Move(sourcePath1, Arg.Any<string>()))
            .Do(x => throw new IOException("File in use"));

        // Act
        await _libraryMover.MoveMangaAsync(
            manga,
            targetLibrary,
            TestContext.Current.CancellationToken
        );

        // Assert
        // Verify error was logged for failed move
        _mockLogger.ReceivedWithAnyArgs().LogError(default(Exception)!, default!);

        // Verify second file was still processed
        _mockFileSystem.Received(1).Move(sourcePath2, Arg.Any<string>());

        // Verify database was still updated
        Assert.Equal(targetLibrary.Id, manga.LibraryId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveMangaAsync_WithNullUpscaledLibraryPath_ShouldLogWarningAndSkip()
    {
        // Arrange
        var sourceLibrary = CreateTestLibrary("Source Library");
        sourceLibrary.UpscaledLibraryPath = null; // Remove upscaled path
        var targetLibrary = CreateTestLibrary("Target Library");
        var manga = CreateTestManga(sourceLibrary, "Test Manga");
        var upscaledChapter = CreateTestChapter(manga, "chapter1.cbz", isUpscaled: true);

        await _dbContext.Libraries.AddRangeAsync(sourceLibrary, targetLibrary);
        await _dbContext.MangaSeries.AddAsync(manga, TestContext.Current.CancellationToken);
        await _dbContext.Chapters.AddAsync(upscaledChapter, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create test file
        var sourcePath = Path.Combine(
            sourceLibrary.NotUpscaledLibraryPath,
            upscaledChapter.RelativePath
        );
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(
            sourcePath,
            "chapter1 content",
            TestContext.Current.CancellationToken
        );

        // Act
        await _libraryMover.MoveMangaAsync(
            manga,
            targetLibrary,
            TestContext.Current.CancellationToken
        );

        // Assert
        // Verify that a warning was logged (logging verification with NSubstitute can be complex due to generics)
        _mockLogger
            .Received()
            .Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o =>
                    o.ToString()!.Contains("Upscaled library path not set for library")
                ),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>()
            );

        // Verify no rename task was enqueued
        await _mockTaskQueue
            .DidNotReceive()
            .EnqueueAsync(Arg.Any<RenameUpscaledChaptersSeriesTask>());

        // Verify regular file was still moved
        _mockFileSystem.Received(1).Move(Arg.Any<string>(), Arg.Any<string>());
    }

    private Library CreateTestLibrary(string name, bool hasUpscaledPath = true)
    {
        var notUpscaledPath = Path.Combine(_tempDir, $"{name}_not_upscaled");
        var upscaledPath = hasUpscaledPath ? Path.Combine(_tempDir, $"{name}_upscaled") : null;

        Directory.CreateDirectory(notUpscaledPath);
        if (upscaledPath != null)
        {
            Directory.CreateDirectory(upscaledPath);
        }

        return new Library
        {
            Id = Random.Shared.Next(1, 10000),
            Name = name,
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
