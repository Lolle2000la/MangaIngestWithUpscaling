using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MangaIngestWithUpscaling.Components.Libraries.Filters;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

public class FiltersTests : TestContext
{
    private ApplicationDbContext _dbContext = null!;

    public FiltersTests()
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
    public void EditLibraryFilterForm_ShouldRenderWithFilterRule()
    {
        // Arrange
        var library = new Library();
        var filterRule = new LibraryFilterRule
        {
            Pattern = "test*",
            PatternType = LibraryFilterPatternType.Contains,
            TargetField = LibraryFilterTargetField.MangaTitle,
            Action = FilterAction.Include,
            Library = library,
        };

        // Act
        var component = RenderComponent<EditLibraryFilterForm>(parameters =>
            parameters.Add(p => p.FilterRule, filterRule)
        );

        // Assert
        Assert.NotNull(component);

        // Should have input field for pattern
        var patternInput = component.Find("input[value='test*']");
        Assert.NotNull(patternInput);

        // Should have selects for different filter options
        var selects = component.FindAll("div.mud-select");
        Assert.True(
            selects.Count >= 3,
            "Should have selects for Pattern Type, Target Field, and Action"
        );
    }

    [Fact]
    public void EditLibraryFilterForm_PatternInput_ShouldBeRequired()
    {
        // Arrange
        var library = new Library();
        var filterRule = new LibraryFilterRule
        {
            Pattern = "",
            PatternType = LibraryFilterPatternType.Contains,
            TargetField = LibraryFilterTargetField.MangaTitle,
            Action = FilterAction.Include,
            Library = library,
        };

        // Act
        var component = RenderComponent<EditLibraryFilterForm>(parameters =>
            parameters.Add(p => p.FilterRule, filterRule)
        );

        // Assert
        var patternInput = component.Find("input");
        Assert.True(patternInput.HasAttribute("required"), "Pattern input should be required");
    }

    [Fact]
    public void EditLibraryFilters_ShouldRenderWithLibrary()
    {
        // Arrange
        var library = new Library { Id = 1, Name = "Test Library" };

        library.FilterRules = new List<LibraryFilterRule>
        {
            new()
            {
                Pattern = "manga*",
                PatternType = LibraryFilterPatternType.Contains,
                TargetField = LibraryFilterTargetField.MangaTitle,
                Action = FilterAction.Include,
                Library = library,
            },
            new()
            {
                Pattern = "skip*",
                PatternType = LibraryFilterPatternType.Regex,
                TargetField = LibraryFilterTargetField.FilePath,
                Action = FilterAction.Exclude,
                Library = library,
            },
        };

        // Act
        var component = RenderComponent<EditLibraryFilters>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Edit Ingest Filters", component.Markup);
        Assert.Contains("Define filters to apply to the files", component.Markup);

        // Should display existing filter rules
        Assert.Contains("manga*", component.Markup);
        Assert.Contains("skip*", component.Markup);

        // Should have table headers
        Assert.Contains("Pattern to match", component.Markup);
        Assert.Contains("Pattern Type", component.Markup);
        Assert.Contains("Target Field", component.Markup);
        Assert.Contains("Action", component.Markup);
    }

    [Fact]
    public void EditLibraryRenameForm_ShouldRenderWithRenameRule()
    {
        // Arrange
        var renameRule = new LibraryRenameRule
        {
            Pattern = "old_name",
            PatternType = LibraryRenamePatternType.Contains,
            TargetField = LibraryRenameTargetField.FileName,
            Replacement = "new_name",
        };

        var mockCallback = EventCallback.Empty;

        // Act
        var component = RenderComponent<EditLibraryRenameForm>(parameters =>
        {
            parameters.Add(p => p.RenameRule, renameRule);
            parameters.Add(p => p.RulesChanged, mockCallback);
        });

        // Assert
        Assert.NotNull(component);

        // Should have input fields for pattern and replacement
        var patternInput = component.Find("input[value='old_name']");
        var replacementInput = component.Find("input[value='new_name']");
        Assert.NotNull(patternInput);
        Assert.NotNull(replacementInput);

        // Should have selects for pattern type and target field
        var selects = component.FindAll("div.mud-select");
        Assert.True(selects.Count >= 2, "Should have selects for Pattern Type and Target Field");
    }

    [Fact]
    public void EditLibraryRenames_ShouldRenderWithLibrary()
    {
        // Arrange
        var library = new Library { Id = 1, Name = "Test Library" };

        library.RenameRules = new ObservableCollection<LibraryRenameRule>
        {
            new()
            {
                Pattern = "Chapter ",
                PatternType = LibraryRenamePatternType.Contains,
                TargetField = LibraryRenameTargetField.ChapterTitle,
                Replacement = "Ch. ",
            },
            new()
            {
                Pattern = "Vol\\.(\\d+)",
                PatternType = LibraryRenamePatternType.Regex,
                TargetField = LibraryRenameTargetField.FileName,
                Replacement = "Volume $1",
            },
        };

        var mockCallback = EventCallback.Empty;

        // Act
        var component = RenderComponent<EditLibraryRenames>(parameters =>
        {
            parameters.Add(p => p.Library, library);
            parameters.Add(p => p.RulesChanged, mockCallback);
        });

        // Assert
        Assert.NotNull(component);
        Assert.Contains("Edit Rename Rules", component.Markup);
        Assert.Contains("Define regex or substring replacements", component.Markup);

        // Should display existing rename rules
        Assert.Contains("Chapter ", component.Markup);
        Assert.Contains("Vol\\.(\\d+)", component.Markup);
        Assert.Contains("Ch. ", component.Markup);
        Assert.Contains("Volume $1", component.Markup);

        // Should have table headers
        Assert.Contains("Pattern", component.Markup);
        Assert.Contains("Pattern Type", component.Markup);
        Assert.Contains("Target Field", component.Markup);
        Assert.Contains("Replacement", component.Markup);
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
