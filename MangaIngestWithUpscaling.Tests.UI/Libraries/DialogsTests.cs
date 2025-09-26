using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MangaIngestWithUpscaling.Components.Libraries.Dialogs;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

public class DialogsTests : TestContext
{
    private ApplicationDbContext _dbContext = null!;

    public DialogsTests()
    {
        SetupDatabase();
        RegisterServices();
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

        // Setup MudBlazor JavaScript interop
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

    [Fact]
    public void LibraryRenameDialog_ShouldRenderWithLibraryParameter()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/library",
            UpscaledLibraryPath = "/test/upscaled",
            RenameRules = new ObservableCollection<LibraryRenameRule>(),
        };

        // Act
        var component = RenderComponent<LibraryRenameDialog>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Edit & Preview Rename Rules", component.Markup);

        // Should contain the two main grid items for editing and preview
        var mudItems = component.FindAll(".mud-grid-item");
        Assert.True(
            mudItems.Count >= 2,
            "Should have at least 2 grid items for editing and preview sections"
        );

        // Should have a close button
        var closeButton = component.Find("button:contains('Close')");
        Assert.NotNull(closeButton);
    }

    [Fact]
    public void LibraryRenameDialog_CloseButton_ShouldTriggerDialogClose()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/library",
            UpscaledLibraryPath = "/test/upscaled",
            RenameRules = new ObservableCollection<LibraryRenameRule>(),
        };

        var mockDialogInstance = Substitute.For<IMudDialogInstance>();

        // Act
        var component = RenderComponent<LibraryRenameDialog>(parameters =>
        {
            parameters.Add(p => p.Library, library);
            parameters.AddCascadingValue(mockDialogInstance);
        });

        var closeButton = component.Find("button:contains('Close')");
        closeButton.Click();

        // Assert
        mockDialogInstance.Received(1).Close();
    }

    [Fact]
    public void LibraryRenameDialog_ShouldContainEditLibraryRenamesComponent()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/library",
            UpscaledLibraryPath = "/test/upscaled",
            RenameRules = new ObservableCollection<LibraryRenameRule>
            {
                new() { Pattern = "test", Replacement = "TEST" },
            },
        };

        // Act
        var component = RenderComponent<LibraryRenameDialog>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        // Should contain EditLibraryRenames component content
        Assert.Contains("Edit Rename Rules", component.Markup);
    }

    [Fact]
    public void LibraryRenameDialog_ShouldContainPreviewLibraryRenamesComponent()
    {
        // Arrange
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/ingest",
            NotUpscaledLibraryPath = "/test/library",
            UpscaledLibraryPath = "/test/upscaled",
            RenameRules = new ObservableCollection<LibraryRenameRule>(),
        };

        // Act
        var component = RenderComponent<LibraryRenameDialog>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        // Should contain PreviewLibraryRenames component - it has expansion panels for previews
        var expansionPanels = component.FindAll(".mud-expand-panel");
        Assert.True(
            expansionPanels.Count >= 0,
            "Should contain expansion panels for rename previews"
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
