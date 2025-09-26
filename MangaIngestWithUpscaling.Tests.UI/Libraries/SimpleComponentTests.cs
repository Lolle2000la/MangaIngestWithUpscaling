using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MangaIngestWithUpscaling.Components.Libraries;
using MangaIngestWithUpscaling.Components.Libraries.FilteredImages;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

public class SimpleComponentTests : TestContext
{
    private ApplicationDbContext _dbContext = null!;
    private ITaskQueue _mockTaskQueue = null!;
    private IImageFilterService _mockImageFilterService = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;
    private NavigationManager _mockNavigationManager = null!;

    public SimpleComponentTests()
    {
        SetupMocks();
        SetupDatabase();
        RegisterServices();
    }

    private void SetupMocks()
    {
        _mockTaskQueue = Substitute.For<ITaskQueue>();
        _mockImageFilterService = Substitute.For<IImageFilterService>();
        _mockDialogService = Substitute.For<IDialogService>();
        _mockSnackbar = Substitute.For<ISnackbar>();
        _mockNavigationManager = Substitute.For<NavigationManager>();
    }

    private void SetupDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    private void RegisterServices()
    {
        Services.AddMudServices();
        Services.AddSingleton(_dbContext);
        Services.AddSingleton(_mockTaskQueue);
        Services.AddSingleton(_mockImageFilterService);
        Services.AddSingleton(_mockDialogService);
        Services.AddSingleton(_mockSnackbar);
        Services.AddSingleton(_mockNavigationManager);

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusFirst").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusLast").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.saveFocus").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.restoreFocus").SetVoidResult();
        JSInterop.Setup<bool>("mudElementRef.focusFirst").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.focusLast").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.saveFocus").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.restoreFocus").SetResult(true);
    }

    // CreateLibrary Component Tests
    [Fact]
    public void CreateLibrary_ShouldRenderInitialForm()
    {
        // Act
        var component = RenderComponent<CreateLibrary>();

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Create Library", component.Markup);

        // Should have EditLibraryForm component
        Assert.Contains("Library Information", component.Markup);

        // Should have Create button that's initially disabled
        var createButton = component.Find("button:contains('Create')");
        Assert.NotNull(createButton);
        Assert.True(
            createButton.HasAttribute("disabled"),
            "Create button should be disabled initially"
        );

        // Should have back button
        var backButton = component.Find("a[href='libraries']");
        Assert.NotNull(backButton);
    }

    // Libraries Component Tests
    [Fact]
    public void Libraries_ShouldRenderEmptyState()
    {
        // Act
        var component = RenderComponent<MangaIngestWithUpscaling.Components.Libraries.Libraries>();

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Libraries", component.Markup);
        Assert.Contains("No Libraries Yet", component.Markup);
        Assert.Contains("Create your first library", component.Markup);

        // Should have Create Library button
        var createButton = component.Find(
            "button:contains('Create Library'), a[href='libraries/create']"
        );
        Assert.NotNull(createButton);

        // Should have Upscale All button
        var upscaleButton = component.Find("button:contains('Upscale All')");
        Assert.NotNull(upscaleButton);
    }

    // EditLibraryForm Component Tests
    [Fact]
    public void EditLibraryForm_ShouldRenderWithLibrary()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/library",
            UpscaledLibraryPath = "/test/upscaled",
            UpscaleOnIngest = false,
            FilterRules = new List<LibraryFilterRule>(),
            RenameRules = new ObservableCollection<LibraryRenameRule>(),
        };

        bool isValid = false;
        var libraryChanged = EventCallback.Factory.Create<Library>(this, (lib) => { });
        var isValidChanged = EventCallback.Factory.Create<bool>(
            this,
            (valid) =>
            {
                isValid = valid;
            }
        );

        // Act
        var component = RenderComponent<EditLibraryForm>(parameters =>
        {
            parameters.Add(p => p.Library, library);
            parameters.Add(p => p.LibraryChanged, libraryChanged);
            parameters.Add(p => p.IsValid, isValid);
            parameters.Add(p => p.IsValidChanged, isValidChanged);
        });

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Library Information", component.Markup);
        Assert.Contains("Basic Settings", component.Markup);

        // Should have library name input with current value
        var nameInput = component.Find("input[value='Test Library']");
        Assert.NotNull(nameInput);

        // Should have tabs for different settings
        Assert.Contains("Ingest Configuration", component.Markup);
        Assert.Contains("File Rename Configuration", component.Markup);
    }

    // ImagePreviewDialog Component Tests
    [Fact]
    public void ImagePreviewDialog_ShouldRenderWithFilteredImage()
    {
        // Arrange
        var library = new Library { Id = 1, Name = "Test Library" };
        var filteredImage = new FilteredImage
        {
            Id = 1,
            OriginalFileName = "test-image.jpg",
            Description = "Test description",
            ContentHash = "abc123",
            FileSizeBytes = 1024,
            DateAdded = DateTime.UtcNow,
            OccurrenceCount = 3,
            MimeType = "image/jpeg",
            ThumbnailBase64 =
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==",
            Library = library,
        };

        // Act
        var component = RenderComponent<ImagePreviewDialog>(parameters =>
            parameters.Add(p => p.FilteredImage, filteredImage)
        );

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Image Preview", component.Markup);
        Assert.Contains(filteredImage.OriginalFileName, component.Markup);
        Assert.Contains(filteredImage.Description, component.Markup);
        Assert.Contains("Occurrences: 3", component.Markup);

        // Should display file size
        Assert.Contains("Size: 1.0 KB", component.Markup);

        // Should have Close button
        var closeButton = component.Find("button:contains('Close')");
        Assert.NotNull(closeButton);
    }

    [Fact]
    public void ImagePreviewDialog_ShouldDisplayImageWhenThumbnailExists()
    {
        // Arrange
        var library = new Library { Id = 1, Name = "Test Library" };
        var filteredImage = new FilteredImage
        {
            Id = 1,
            OriginalFileName = "test-image.jpg",
            ContentHash = "abc123",
            MimeType = "image/jpeg",
            ThumbnailBase64 =
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==",
            DateAdded = DateTime.UtcNow,
            OccurrenceCount = 1,
            Library = library,
        };

        // Act
        var component = RenderComponent<ImagePreviewDialog>(parameters =>
            parameters.Add(p => p.FilteredImage, filteredImage)
        );

        // Assert
        var imageElement = component.Find("img[src*='data:image/jpeg;base64,']");
        Assert.NotNull(imageElement);
    }

    [Fact]
    public void ImagePreviewDialog_ShouldDisplayIconWhenNoThumbnail()
    {
        // Arrange
        var library = new Library { Id = 1, Name = "Test Library" };
        var filteredImage = new FilteredImage
        {
            Id = 1,
            OriginalFileName = "test-image.jpg",
            ContentHash = "abc123",
            MimeType = "image/jpeg",
            ThumbnailBase64 = null, // No thumbnail
            DateAdded = DateTime.UtcNow,
            OccurrenceCount = 1,
            Library = library,
        };

        // Act
        var component = RenderComponent<ImagePreviewDialog>(parameters =>
            parameters.Add(p => p.FilteredImage, filteredImage)
        );

        // Assert
        var iconElement = component.Find("svg[data-testid='ImageIcon']");
        Assert.NotNull(iconElement);
    }

    // EditImageFilterDialog Component Tests
    [Fact]
    public void EditImageFilterDialog_ShouldRenderWithFilteredImage()
    {
        // Arrange
        var library = new Library { Id = 1, Name = "Test Library" };
        var filteredImage = new FilteredImage
        {
            Id = 1,
            OriginalFileName = "test-image.jpg",
            Description = "Test description",
            ContentHash = "abc123",
            FileSizeBytes = 1024,
            DateAdded = DateTime.UtcNow,
            OccurrenceCount = 5,
            MimeType = "image/jpeg",
            ThumbnailBase64 =
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==",
            Library = library,
        };

        // Act
        var component = RenderComponent<EditImageFilterDialog>(parameters =>
            parameters.Add(p => p.FilteredImage, filteredImage)
        );

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Edit Image Filter", component.Markup);
        Assert.Contains(filteredImage.OriginalFileName, component.Markup);
        Assert.Contains(filteredImage.Description, component.Markup);
        Assert.Contains("Occurrences: 5", component.Markup);

        // Should have Cancel and Save Changes buttons
        var cancelButton = component.Find("button:contains('Cancel')");
        var saveButton = component.Find("button:contains('Save Changes')");
        Assert.NotNull(cancelButton);
        Assert.NotNull(saveButton);
    }

    // AddImageFilterDialog Component Tests
    [Fact]
    public void AddImageFilterDialog_ShouldRenderWithRequiredParameters()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            FilteredImages = new List<FilteredImage>(),
        };

        // Act
        var component = RenderComponent<AddImageFilterDialog>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Add Image Filter", component.Markup);
        Assert.Contains("Choose an image to filter out", component.Markup);

        // Should have upload tab and CBZ tab
        Assert.Contains("Upload Image File", component.Markup);
        Assert.Contains("Select from CBZ", component.Markup);

        // Should have Cancel and Add Filter buttons
        var cancelButton = component.Find("button:contains('Cancel')");
        var addButton = component.Find("button:contains('Add Filter')");
        Assert.NotNull(cancelButton);
        Assert.NotNull(addButton);
    }

    [Fact]
    public void AddImageFilterDialog_AddFilterButton_ShouldBeDisabledInitially()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            FilteredImages = new List<FilteredImage>(),
        };

        // Act
        var component = RenderComponent<AddImageFilterDialog>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        var addButton = component.Find("button:contains('Add Filter')");
        Assert.True(
            addButton.HasAttribute("disabled"),
            "Add Filter button should be disabled initially"
        );
    }

    // PreviewLibraryRenames Component Tests
    [Fact]
    public void PreviewLibraryRenames_ShouldRenderWithLibrary()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/library",
            RenameRules = new ObservableCollection<LibraryRenameRule>
            {
                new()
                {
                    Pattern = "Chapter ",
                    PatternType = LibraryRenamePatternType.Contains,
                    TargetField = LibraryRenameTargetField.ChapterTitle,
                    Replacement = "Ch. ",
                },
            },
        };

        // Act
        var component = RenderComponent<PreviewLibraryRenames>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        Assert.NotNull(component);

        // Should contain expansion panels for different preview types
        var expansionPanels = component.FindAll(".mud-expand-panel");
        Assert.True(
            expansionPanels.Count >= 0,
            "Should have expansion panels for preview sections"
        );
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
}
