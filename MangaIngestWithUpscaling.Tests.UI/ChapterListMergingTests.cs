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
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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