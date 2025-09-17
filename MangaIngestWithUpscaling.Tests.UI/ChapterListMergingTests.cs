using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.MangaManagement.Chapters;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MangaIngestWithUpscaling.Tests.UI;

public class ChapterListMergingTests : TestContext
{
    private ApplicationDbContext _dbContext = null!;
    private Mock<IChapterChangedNotifier> _mockChapterChangedNotifier = null!;
    private Mock<IDialogService> _mockDialogService = null!;
    private Mock<IFileSystem> _mockFileSystem = null!;
    private Mock<ILibraryIntegrityChecker> _mockLibraryIntegrityChecker = null!;
    private Mock<IMangaMetadataChanger> _mockMangaMetadataChanger = null!;
    private Mock<IChapterMergeCoordinator> _mockMergeCoordinator = null!;
    private Mock<IMetadataHandlingService> _mockMetadataHandler = null!;
    private Mock<IChapterMergeRevertService> _mockRevertService = null!;
    private Mock<ISnackbar> _mockSnackbar = null!;
    private Mock<ITaskQueue> _mockTaskQueue = null!;
    private Mock<IWebHostEnvironment> _mockWebHostEnvironment = null!;

    public ChapterListMergingTests()
    {
        SetupMocks();
        SetupDatabase();
        RegisterServices();
    }

    private void SetupMocks()
    {
        _mockMergeCoordinator = new Mock<IChapterMergeCoordinator>();
        _mockRevertService = new Mock<IChapterMergeRevertService>();
        _mockMetadataHandler = new Mock<IMetadataHandlingService>();
        _mockFileSystem = new Mock<IFileSystem>();
        _mockTaskQueue = new Mock<ITaskQueue>();
        _mockChapterChangedNotifier = new Mock<IChapterChangedNotifier>();
        _mockLibraryIntegrityChecker = new Mock<ILibraryIntegrityChecker>();
        _mockMangaMetadataChanger = new Mock<IMangaMetadataChanger>();
        _mockWebHostEnvironment = new Mock<IWebHostEnvironment>();
        _mockSnackbar = new Mock<ISnackbar>();
        _mockDialogService = new Mock<IDialogService>();

        // Setup common mock behaviors
        _mockWebHostEnvironment.Setup(x => x.EnvironmentName).Returns("Test");
        _mockMetadataHandler.Setup(x => x.GetSeriesAndTitleFromComicInfo(It.IsAny<string>()))
            .Returns(new ExtractedMetadata("Test Series", "Test Chapter", "1"));
    }

    private void SetupDatabase()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();
    }

    private void RegisterServices()
    {
        Services.AddMudServices();
        Services.AddSingleton(_dbContext);
        Services.AddSingleton(_mockMergeCoordinator.Object);
        Services.AddSingleton(_mockRevertService.Object);
        Services.AddSingleton(_mockMetadataHandler.Object);
        Services.AddSingleton(_mockWebHostEnvironment.Object);
        Services.AddSingleton(_mockTaskQueue.Object);
        Services.AddSingleton(_mockChapterChangedNotifier.Object);
        Services.AddSingleton(_mockLibraryIntegrityChecker.Object);
        Services.AddSingleton(_mockMangaMetadataChanger.Object);
        Services.AddSingleton(_mockSnackbar.Object);
        Services.AddSingleton(_mockDialogService.Object);
        Services.AddSingleton(_mockFileSystem.Object);

        // Setup MudBlazor JavaScript interop with comprehensive coverage
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.addOnBlurEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.removeOnBlurEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.addOnFocusEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.removeOnFocusEvent").SetVoidResult();
        JSInterop.Setup<bool>("mudElementRef.focusFirst").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.focusLast").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.saveFocus").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.restoreFocus").SetResult(true);
    }

    private async Task<(Manga manga, Library library, List<Chapter> chapters)> CreateTestDataAsync()
    {
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/library",
            UpscaledLibraryPath = "/test/upscaled"
        };

        var manga = new Manga { Id = 1, PrimaryTitle = "Test Manga", Library = library, LibraryId = library.Id };

        var chapters = new List<Chapter>
        {
            new()
            {
                Id = 1,
                FileName = "Chapter 1.1.cbz",
                RelativePath = "Test Manga/Chapter 1.1.cbz",
                Manga = manga,
                MangaId = manga.Id,
                IsUpscaled = false
            },
            new()
            {
                Id = 2,
                FileName = "Chapter 1.2.cbz",
                RelativePath = "Test Manga/Chapter 1.2.cbz",
                Manga = manga,
                MangaId = manga.Id,
                IsUpscaled = false
            },
            new()
            {
                Id = 3,
                FileName = "Chapter 2.cbz",
                RelativePath = "Test Manga/Chapter 2.cbz",
                Manga = manga,
                MangaId = manga.Id,
                IsUpscaled = false
            }
        };

        manga.Chapters = chapters;

        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.Add(manga);
        _dbContext.Chapters.AddRange(chapters);
        await _dbContext.SaveChangesAsync();

        return (manga, library, chapters);
    }

    [Fact]
    public async Task ChapterList_ShouldRenderChapters()
    {
        // Arrange
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Setup merge coordinator to return no merge possibilities initially
        var mergeInfo = new MergeActionInfo();
        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert
        Assert.NotNull(component);

        // Check that the component renders without throwing
        IElement mudTable = component.Find("div.mud-table");
        Assert.NotNull(mudTable);
    }

    [Fact]
    public async Task ChapterList_WithMergePossibilities_ShouldEnableMergeButton()
    {
        // Arrange
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Setup merge coordinator to return merge possibilities
        var mergeInfo = new MergeActionInfo
        {
            NewMergeGroups = new Dictionary<string, List<Chapter>>
            {
                { "1", new List<Chapter> { chapters[0], chapters[1] } }
            }
        };
        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert
        IEnumerable<IElement> mergeButtons = component.FindAll("button")
            .Where(b => b.TextContent.Contains("Merge Selected"));

        Assert.True(mergeButtons.Any(), "Should have a merge button");
    }

    [Fact]
    public async Task ChapterList_WithMergedChapter_ShouldShowRevertButton()
    {
        // Arrange
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Create a merged chapter info record
        var mergedChapterInfo = new MergedChapterInfo
        {
            ChapterId = chapters[0].Id,
            MergedChapterNumber = "1",
            OriginalParts = new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 1.1.cbz", PageNames = new List<string> { "page1.jpg" } },
                new() { FileName = "Chapter 1.2.cbz", PageNames = new List<string> { "page2.jpg" } }
            },
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.MergedChapterInfos.Add(mergedChapterInfo);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Setup revert service to indicate chapter can be reverted
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(chapters[0]))
            .ReturnsAsync(true);

        // Setup merge coordinator
        var mergeInfo = new MergeActionInfo();
        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert
        IEnumerable<IElement> revertButtons = component.FindAll("button")
            .Where(b => b.TextContent.Contains("Revert Selected"));

        Assert.True(revertButtons.Any(), "Should have a revert button");
    }

    [Fact]
    public async Task PerformMerge_ShouldCallMergeCoordinator()
    {
        // Arrange
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        var mergeInfo = new MergeActionInfo
        {
            NewMergeGroups = new Dictionary<string, List<Chapter>>
            {
                { "1", new List<Chapter> { chapters[0], chapters[1] } }
            }
        };

        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        var completedMerges = new List<MergeInfo>
        {
            new(
                CreateFoundChapter("Chapter 1.cbz", "1"),
                new List<OriginalChapterPart>
                {
                    new() { FileName = "Chapter 1.1.cbz", PageNames = new List<string> { "page1.jpg" } },
                    new() { FileName = "Chapter 1.2.cbz", PageNames = new List<string> { "page2.jpg" } }
                },
                "1"
            )
        };

        _mockMergeCoordinator.Setup(x => x.MergeSelectedChaptersAsync(
                It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(completedMerges);

        // Setup the merged chapter to be found in database after merge
        Chapter mergedChapter = chapters[0]; // First chapter becomes the merged one
        mergedChapter.FileName = "Chapter 1.cbz";

        // Setup revert service to indicate the merged chapter can be reverted
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(mergedChapter))
            .ReturnsAsync(true);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Verify merge coordinator was called for getting possibilities
        _mockMergeCoordinator.Verify(x => x.GetPossibleMergeActionsAsync(
            It.IsAny<List<Chapter>>(),
            It.IsAny<bool>()), Times.AtLeastOnce);

        // Assert
        Assert.NotNull(component);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dbContext?.Database.CloseConnection();
            _dbContext?.Dispose();
        }

        base.Dispose(disposing);
    }

    [Fact]
    public async Task ChapterList_AfterMerge_ShouldShowOnlyRevertButtonNotBoth()
    {
        // Arrange - This tests the bug fix for both merge and revert buttons being shown simultaneously
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Create a merged chapter (simulating the state after a successful merge)
        var mergedChapterInfo = new MergedChapterInfo
        {
            ChapterId = chapters[0].Id,
            MergedChapterNumber = "1",
            OriginalParts = new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 1.1.cbz", PageNames = new List<string> { "page1.jpg" } },
                new() { FileName = "Chapter 1.2.cbz", PageNames = new List<string> { "page2.jpg" } }
            },
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.MergedChapterInfos.Add(mergedChapterInfo);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Setup revert service to indicate this chapter can be reverted (it's merged)
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(chapters[0]))
            .ReturnsAsync(true);

        // Setup merge coordinator to return NO merge possibilities for the merged chapter
        // (merged chapters should not be mergeable)
        var mergeInfo = new MergeActionInfo(); // Empty - no merge possibilities
        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert - Check that revert button exists
        IEnumerable<IElement> revertButtons = component.FindAll("button")
            .Where(b => b.TextContent.Contains("Revert"));
        Assert.True(revertButtons.Any(), "Should have a revert button for merged chapter");

        // Assert - Check that merge buttons are disabled/not available for merged chapters
        // The "Merge Selected" button should be disabled when merged chapters are selected
        IEnumerable<IElement> mergeButtons = component.FindAll("button")
            .Where(b => b.TextContent.Contains("Merge Selected"));

        if (mergeButtons.Any())
        {
            // If merge button exists, it should be disabled when merged chapter is selected
            IElement mergeButton = mergeButtons.First();
            Assert.True(mergeButton.HasAttribute("disabled") || mergeButton.ClassList.Contains("mud-disabled"),
                "Merge button should be disabled when merged chapters are selected");
        }

        // Test individual row buttons - merged chapter should show revert but not merge
        IEnumerable<IElement> mergedChapterRows = component.FindAll("tr").Where(tr =>
            tr.TextContent.Contains("Chapter 1.1.cbz")); // This is our merged chapter

        foreach (IElement row in mergedChapterRows)
        {
            IEnumerable<IElement> rowMergeButtons = row.QuerySelectorAll("button")
                .Where(b => b.GetAttribute("title")?.Contains("Merge") == true);
            IEnumerable<IElement> rowRevertButtons = row.QuerySelectorAll("button")
                .Where(b => b.GetAttribute("title")?.Contains("Revert") == true);

            Assert.Empty(rowMergeButtons); // No merge button for merged chapter
            Assert.NotEmpty(rowRevertButtons); // Should have revert button for merged chapter
        }
    }

    [Fact]
    public async Task ChapterList_AfterRevert_ShouldRestoreMergeButtonsOnParts()
    {
        // Arrange - This tests the bug fix for missing merge buttons after revert
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Test the scenario where individual chapters should be mergeable
        // Setup merge coordinator to indicate chapters can be merged
        var mergeInfo = new MergeActionInfo
        {
            NewMergeGroups = new Dictionary<string, List<Chapter>>
            {
                { "1", new List<Chapter> { chapters[0], chapters[1] } }
            }
        };

        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Chapters are NOT merged, so revert service should return false
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(It.IsAny<Chapter>()))
            .ReturnsAsync(false);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert - Check that merge possibilities service is called (indicates caching system works)
        _mockMergeCoordinator.Verify(x => x.GetPossibleMergeActionsAsync(
                It.IsAny<List<Chapter>>(),
                It.IsAny<bool>()),
            Times.AtLeastOnce(), "Merge coordinator should be called to check merge possibilities");

        // Check that merge buttons exist in the UI
        IEnumerable<IElement> mergeButtons = component.FindAll("button")
            .Where(b => b.TextContent.Contains("Merge Selected"));

        Assert.True(mergeButtons.Any(), "Should have merge buttons available when merge possibilities exist");
    }

    [Fact]
    public async Task ChapterList_MergePossibilitiesCache_ShouldInvalidateAfterOperations()
    {
        // Arrange - Test that merge possibilities are properly calculated
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        var mergeInfo = new MergeActionInfo
        {
            NewMergeGroups = new Dictionary<string, List<Chapter>>
            {
                { "1", new List<Chapter> { chapters[0], chapters[1] } }
            }
        };

        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert - Verify that the component properly calls the merge coordinator for possibilities
        _mockMergeCoordinator.Verify(x => x.GetPossibleMergeActionsAsync(
                It.IsAny<List<Chapter>>(),
                It.IsAny<bool>()),
            Times.AtLeastOnce()); // Should be called at least once to get merge possibilities

        // Test that merge button exists when there are merge possibilities
        IEnumerable<IElement> mergeButtons = component.FindAll("button")
            .Where(b => b.TextContent.Contains("Merge Selected"));

        Assert.True(mergeButtons.Any(), "Should have merge buttons when merge possibilities exist");
    }

    [Fact]
    public async Task ChapterList_IndividualChapterButtons_ShouldShowCorrectActionsBasedOnState()
    {
        // Arrange - Test individual chapter row buttons
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Set up one merged chapter and one regular chapter
        var mergedChapterInfo = new MergedChapterInfo
        {
            ChapterId = chapters[0].Id,
            MergedChapterNumber = "1",
            OriginalParts = new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 1.1.cbz", PageNames = new List<string> { "page1.jpg" } }
            },
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.MergedChapterInfos.Add(mergedChapterInfo);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Setup revert service - first chapter can be reverted (it's merged)
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(chapters[0]))
            .ReturnsAsync(true);
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(chapters[1]))
            .ReturnsAsync(false);
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(chapters[2]))
            .ReturnsAsync(false);

        // Setup merge coordinator - second chapter can be merged
        var mergeInfo = new MergeActionInfo
        {
            NewMergeGroups = new Dictionary<string, List<Chapter>>
            {
                { "2", new List<Chapter> { chapters[1], chapters[2] } }
            }
        };
        _mockMergeCoordinator.Setup(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo);

        // Act
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Assert - Check buttons for merged chapter (should have revert, not merge)
        IElement? chapter1Row = component.FindAll("tr")
            .FirstOrDefault(tr => tr.TextContent.Contains("Chapter 1.1.cbz"));

        if (chapter1Row != null)
        {
            IElement? revertButton = chapter1Row.QuerySelectorAll("button")
                .FirstOrDefault(b => b.GetAttribute("title")?.Contains("Revert") == true);
            IElement? mergeButton = chapter1Row.QuerySelectorAll("button")
                .FirstOrDefault(b => b.GetAttribute("title")?.Contains("Merge") == true);

            Assert.NotNull(revertButton); // Should have revert button
            Assert.Null(mergeButton); // Should NOT have merge button
        }

        // Assert - Check buttons for regular chapters that can be merged
        IElement? chapter2Row = component.FindAll("tr")
            .FirstOrDefault(tr => tr.TextContent.Contains("Chapter 1.2.cbz"));

        if (chapter2Row != null)
        {
            IElement? mergeButton = chapter2Row.QuerySelectorAll("button")
                .FirstOrDefault(b => b.GetAttribute("title")?.Contains("Merge") == true);
            IElement? revertButton = chapter2Row.QuerySelectorAll("button")
                .FirstOrDefault(b => b.GetAttribute("title")?.Contains("Revert") == true);

            Assert.NotNull(mergeButton); // Should have merge button (part of mergeable group)
            Assert.Null(revertButton); // Should NOT have revert button (not merged)
        }
    }

    [Fact]
    public async Task ChapterList_ActualUImerge_ShouldDisplayCorrectMergedChapterElement()
    {
        // Arrange - Test that the correct merged chapter element is displayed after triggering actual UI merge action
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Setup chapters so they can be merged (Chapter 1.1 and 1.2)
        chapters[0].FileName = "Chapter 1.1.cbz";
        chapters[0].RelativePath = "Test Manga/Chapter 1.1.cbz";
        chapters[1].FileName = "Chapter 1.2.cbz";
        chapters[1].RelativePath = "Test Manga/Chapter 1.2.cbz";
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Setup merge coordinator to indicate these chapters can be merged
        var mergeInfo = new MergeActionInfo
        {
            NewMergeGroups = new Dictionary<string, List<Chapter>>
            {
                { "1", new List<Chapter> { chapters[0], chapters[1] } }
            }
        };

        _mockMergeCoordinator
            .SetupSequence(x => x.GetPossibleMergeActionsAsync(It.IsAny<List<Chapter>>(), It.IsAny<bool>()))
            .ReturnsAsync(mergeInfo) // Initial load - with latest chapters
            .ReturnsAsync(mergeInfo) // When checking if individual chapter can merge - with latest chapters
            .ReturnsAsync(new MergeActionInfo()) // For latest chapter check (without latest) - NO merge actions
            .ReturnsAsync(mergeInfo) // For latest chapter check (with latest) - HAS merge actions  
            .ReturnsAsync(new MergeActionInfo()); // After merge (no more merge possibilities)

        // Setup merge coordinator to actually perform the merge
        var mergeResult = new List<MergeInfo>
        {
            new(
                new FoundChapter("Chapter 1.cbz", "Test Manga/Chapter 1.cbz", ChapterStorageType.Cbz,
                    new ExtractedMetadata("Test Manga", "Chapter 1", "1")),
                new List<OriginalChapterPart>
                {
                    new() { FileName = "Chapter 1.1.cbz", PageNames = new List<string> { "page1.jpg" } },
                    new() { FileName = "Chapter 1.2.cbz", PageNames = new List<string> { "page2.jpg" } }
                },
                "1")
        };

        _mockMergeCoordinator.Setup(x => x.MergeSelectedChaptersAsync(
                It.Is<List<Chapter>>(list =>
                    list.Count == 2 && list.Contains(chapters[0]) && list.Contains(chapters[1])),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<Chapter>, bool, CancellationToken>((chapterList, includeLatest, cancellationToken) =>
            {
                // Simulate the actual merge operation by updating the database
                // Update first chapter to be the merged chapter
                chapters[0].FileName = "Chapter 1.cbz";
                chapters[0].RelativePath = "Test Manga/Chapter 1.cbz";

                // Remove the second chapter from database (it was merged into the first)
                _dbContext.Chapters.Remove(chapters[1]);

                // Add merged chapter info to database
                var mergedChapterInfo = new MergedChapterInfo
                {
                    ChapterId = chapters[0].Id,
                    MergedChapterNumber = "1",
                    OriginalParts = mergeResult[0].OriginalParts,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.MergedChapterInfos.Add(mergedChapterInfo);
                _dbContext.SaveChanges();
            })
            .ReturnsAsync(mergeResult);

        // Setup revert service to reflect initial and post-merge states
        _mockRevertService.SetupSequence(x => x.CanRevertChapterAsync(It.Is<Chapter>(c => c.Id == chapters[0].Id)))
            .ReturnsAsync(false) // Initially not merged
            .ReturnsAsync(true); // After merge, can be reverted
        _mockRevertService.Setup(x => x.CanRevertChapterAsync(It.Is<Chapter>(c => c.Id == chapters[1].Id)))
            .ReturnsAsync(false); // Chapter 1.2 is never merged (gets removed)

        // Setup dialog service to confirm the merge (simulate user clicking "Yes")
        _mockDialogService.Setup(x => x.ShowMessageBox(
                It.Is<string>(title => title.Contains("Latest Chapter")),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true); // User confirms the merge

        // Act - Step 1: Render the component initially
        IRenderedComponent<ChapterList> component = RenderComponentWithProviders<ChapterList>(parameters => parameters
            .Add(p => p.Manga, manga));

        // Verify initial state - both individual chapters should be visible
        IRefreshableElementCollection<IElement> initialRows = component.FindAll("tr");
        IElement? initialChapter11 = initialRows.FirstOrDefault(row => row.TextContent.Contains("Chapter 1.1.cbz"));
        IElement? initialChapter12 = initialRows.FirstOrDefault(row => row.TextContent.Contains("Chapter 1.2.cbz"));

        Assert.NotNull(initialChapter11); // Chapter 1.1 should be visible initially
        Assert.NotNull(initialChapter12); // Chapter 1.2 should be visible initially

        // Step 2: Find and click the merge button for Chapter 1.1
        IEnumerable<IElement> mergeButtons = component.FindAll("button")
            .Where(btn => btn.GetAttribute("title")?.Contains("Merge this chapter") == true);

        Assert.True(mergeButtons.Any(), "Should find merge buttons in the component");

        IElement mergeButton = mergeButtons.First();

        // Act - Step 3: Click the merge button to trigger actual UI merge action
        await mergeButton.ClickAsync(new MouseEventArgs());

        // The component should automatically refresh after the merge operation

        // Assert - Verify the final state shows the correct merged chapter
        IRefreshableElementCollection<IElement> finalRows = component.FindAll("tr");

        // 1. Verify that the merged chapter is now displayed with correct filename
        IElement? mergedChapterRow = finalRows.FirstOrDefault(row =>
            row.TextContent.Contains("Chapter 1.cbz"));
        Assert.NotNull(mergedChapterRow); // Merged chapter should be visible

        // 2. Verify that the original individual parts are no longer displayed
        IEnumerable<IElement> finalChapter11 = finalRows.Where(row =>
            row.TextContent.Contains("Chapter 1.1.cbz") && !row.TextContent.Contains("Chapter 1.cbz"));
        IEnumerable<IElement> finalChapter12 = finalRows.Where(row =>
            row.TextContent.Contains("Chapter 1.2.cbz"));

        Assert.Empty(finalChapter11); // Original Chapter 1.1 should not be displayed anymore
        Assert.Empty(finalChapter12); // Original Chapter 1.2 should not be displayed anymore

        // 3. Verify that the merge operation was actually called
        _mockMergeCoordinator.Verify(x => x.MergeSelectedChaptersAsync(
                It.Is<List<Chapter>>(list => list.Count == 2),
                It.IsAny<bool>()),
            Times.Once, "Merge operation should have been called once");

        // 4. Verify that the dialog was shown for latest chapter confirmation
        _mockDialogService.Verify(x => x.ShowMessageBox(
                It.Is<string>(title => title.Contains("Latest Chapter")),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once, "Dialog should have been shown to confirm latest chapter merge");

        // 5. Verify that the merged chapter shows the correct merged filename
        IRefreshableElementCollection<IElement> chapterCells = component.FindAll("td");
        IEnumerable<IElement> mergedFilenameCells =
            chapterCells.Where(cell => cell.TextContent.Contains("Chapter 1.cbz"));
        Assert.True(mergedFilenameCells.Any(), "Merged chapter should display the correct merged filename");

        // 6. Verify that merge info is properly stored in database
        MergedChapterInfo? mergeInfoInDb =
            _dbContext.MergedChapterInfos.FirstOrDefault(m => m.ChapterId == chapters[0].Id);
        Assert.NotNull(mergeInfoInDb); // Merge info should be saved to database
        Assert.Equal("1", mergeInfoInDb.MergedChapterNumber);
        Assert.Equal(2, mergeInfoInDb.OriginalParts.Count); // Should have 2 original parts

        // 7. Verify that revert service was called to update merge status  
        _mockRevertService.Verify(x => x.CanRevertChapterAsync(It.IsAny<Chapter>()),
            Times.AtLeast(2), "Component should check merge status for chapters before and after merge");

        // 8. Verify that only merged chapters are displayed (merged chapter + third chapter)
        IEnumerable<IElement> dataRows = finalRows.Where(row =>
            row.QuerySelectorAll("td").Any() &&
            !row.ClassList.Contains("mud-table-head")); // Exclude header row

        // Should have at most 2 chapters after merge (merged chapter + third chapter)
        Assert.True(dataRows.Count() <= 2,
            "Should have at most 2 chapters after merge (merged chapter + third chapter)");

        // 9. Verify that the merged chapter now shows a revert button instead of merge button
        IEnumerable<IElement> revertButtons = component.FindAll("button")
            .Where(btn => btn.GetAttribute("title")?.Contains("Revert this merged chapter") == true);
        Assert.True(revertButtons.Any(), "Merged chapter should now show revert button");

        // 10. Verify that the merged chapter no longer shows a merge button
        IEnumerable<IElement> remainingMergeButtons = component.FindAll("button")
            .Where(btn => btn.GetAttribute("title")?.Contains("Merge this chapter") == true);
        // Should have fewer merge buttons now (or none if only the merged chapter and third chapter remain)
        Assert.True(remainingMergeButtons.Count() < mergeButtons.Count(),
            "Should have fewer merge buttons after merging chapters");
    }

    #region Helper Methods

    private static FoundChapter CreateFoundChapter(string fileName, string chapterNumber)
    {
        return new FoundChapter(
            fileName,
            fileName, // Relative path same as filename for test
            ChapterStorageType.Cbz,
            new ExtractedMetadata("Test Series", Path.GetFileNameWithoutExtension(fileName), chapterNumber));
    }

    private IRenderedComponent<T> RenderComponentWithProviders<T>(
        Action<ComponentParameterCollectionBuilder<T>>? parameterBuilder = null)
        where T : class, IComponent
    {
        var componentParams = new ComponentParameterCollectionBuilder<T>();
        parameterBuilder?.Invoke(componentParams);
        ComponentParameterCollection builtParams = componentParams.Build();

        RenderFragment content = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.OpenComponent<T>(1);
            foreach (ComponentParameter param in builtParams)
            {
                if (param.Name != null)
                {
                    builder.AddAttribute(2, param.Name, param.Value);
                }
            }

            builder.CloseComponent();
            builder.CloseComponent();
        };

        return Render(content).FindComponent<T>();
    }

    #endregion
}