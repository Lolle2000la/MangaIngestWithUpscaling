using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.Libraries;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

/// <summary>
/// Interaction tests for <see cref="EditLibrary"/>: the page loads a detached graph, the user edits
/// it, and saving must sync every child collection into the tracked graph. These replace the old
/// "renders without throwing" checks with real persistence assertions.
/// </summary>
public class EditLibraryTests : LibraryTestBase
{
    [Fact]
    public async Task Save_PersistsScalarsAndSyncsAddedEditedAndRemovedChildren()
    {
        Library library = await SeedLibraryAsync();
        int removedIngestId = library.IngestPaths[1].Id;
        int removedFilterId = library.FilterRules[1].Id;
        int keptRenameId = library.RenameRules[0].Id;
        int removedRenameId = library.RenameRules[1].Id;

        var component = RenderWithProviders<EditLibrary>(parameters =>
            parameters.Add(p => p.LibraryId, library.Id)
        );
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Instance.Library);
            Assert.Equal(library.Id, component.Instance.Library!.Id);
        });

        Library edited = component.Instance.Library!;
        edited.Name = "Renamed Library";
        edited.MergeChapterParts = true;
        edited.StripDetectionMode = StripDetectionMode.DetectAndApply;
        edited.NotUpscaledLibraryPath = CreateTempDir("not-upscaled-edited");
        edited.UpscaledLibraryPath = CreateTempDir("upscaled-edited");

        // Ingest paths: edit the first, drop the second, add a third.
        string editedIngestPath = CreateTempDir("ingest-edited");
        string addedIngestPath = CreateTempDir("ingest-added");
        edited.IngestPaths[0].Path = editedIngestPath;
        edited.IngestPaths[0].SortOrder = 7;
        edited.IngestPaths.RemoveAll(p => p.Id == removedIngestId);
        edited.IngestPaths.Add(new LibraryIngestPath { Path = addedIngestPath, SortOrder = 3 });

        // Filter rules: edit the first, drop the second, add a third.
        edited.FilterRules[0].Pattern = "edited-filter";
        edited.FilterRules.RemoveAll(f => f.Id == removedFilterId);
        edited.FilterRules.Add(
            new LibraryFilterRule
            {
                Pattern = "added-filter",
                Library = null!,
                PatternType = LibraryFilterPatternType.Regex,
                TargetField = LibraryFilterTargetField.ChapterTitle,
                Action = FilterAction.Exclude,
            }
        );

        // Rename rules: edit the first, drop the second, add a third.
        edited.RenameRules[0].Pattern = "edited-rename";
        edited.RenameRules.Remove(edited.RenameRules.First(r => r.Id == removedRenameId));
        edited.RenameRules.Add(
            new LibraryRenameRule
            {
                Pattern = "added-rename",
                PatternType = LibraryRenamePatternType.Regex,
                TargetField = LibraryRenameTargetField.FileName,
                Replacement = "added",
            }
        );

        component.Instance.IsValid = true;
        component.Render(parameters => parameters.Add(p => p.LibraryId, library.Id));

        IElement saveButton = component
            .FindAll("button")
            .First(b => b.TextContent.Contains("Save Changes", StringComparison.Ordinal));
        Assert.False(saveButton.HasAttribute("disabled"));
        await component.InvokeAsync(() => saveButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(
            () =>
            {
                var navigation = Services.GetRequiredService<NavigationManager>();
                Assert.EndsWith("/libraries", navigation.Uri);
            },
            TimeSpan.FromSeconds(15)
        );

        await using var db = await _testDb.Database.CreateContextAsync();
        Library persisted = await db
            .Libraries.Include(l => l.IngestPaths)
            .Include(l => l.FilterRules)
            .Include(l => l.RenameRules)
            .FirstAsync(l => l.Id == library.Id);

        Assert.Equal("Renamed Library", persisted.Name);
        Assert.False(persisted.UpscaleOnIngest);
        Assert.True(persisted.MergeChapterParts);
        Assert.Equal(StripDetectionMode.DetectAndApply, persisted.StripDetectionMode);
        Assert.Equal(edited.NotUpscaledLibraryPath, persisted.NotUpscaledLibraryPath);
        Assert.Equal(edited.UpscaledLibraryPath, persisted.UpscaledLibraryPath);

        // Ingest paths: the edit survived, the removal stuck and the addition was inserted.
        Assert.Equal(2, persisted.IngestPaths.Count);
        Assert.DoesNotContain(persisted.IngestPaths, p => p.Id == removedIngestId);
        LibraryIngestPath keptIngest = persisted.IngestPaths.Single(p =>
            p.Path == editedIngestPath
        );
        Assert.Equal(7, keptIngest.SortOrder);
        Assert.Contains(persisted.IngestPaths, p => p.Path == addedIngestPath);

        // Filter rules.
        Assert.Equal(2, persisted.FilterRules.Count);
        Assert.DoesNotContain(persisted.FilterRules, f => f.Id == removedFilterId);
        LibraryFilterRule editedFilter = persisted.FilterRules.Single(f =>
            f.Pattern == "edited-filter"
        );
        Assert.Equal(LibraryFilterPatternType.Contains, editedFilter.PatternType);
        LibraryFilterRule addedFilter = persisted.FilterRules.Single(f =>
            f.Pattern == "added-filter"
        );
        Assert.Equal(LibraryFilterPatternType.Regex, addedFilter.PatternType);
        Assert.Equal(FilterAction.Exclude, addedFilter.Action);

        // Rename rules.
        Assert.Equal(2, persisted.RenameRules.Count);
        Assert.DoesNotContain(persisted.RenameRules, r => r.Id == removedRenameId);
        LibraryRenameRule keptRename = persisted.RenameRules.Single(r => r.Id == keptRenameId);
        Assert.Equal("edited-rename", keptRename.Pattern);
        LibraryRenameRule addedRename = persisted.RenameRules.Single(r =>
            r.Pattern == "added-rename"
        );
        Assert.Equal("added", addedRename.Replacement);
    }

    [Fact]
    public async Task Save_RemovingAChild_DeletesItWithoutRecreatingIt()
    {
        Library library = await SeedLibraryAsync();
        int removedIngestId = library.IngestPaths[1].Id;
        int removedFilterId = library.FilterRules[1].Id;
        int removedRenameId = library.RenameRules[1].Id;

        var component = RenderWithProviders<EditLibrary>(parameters =>
            parameters.Add(p => p.LibraryId, library.Id)
        );
        component.WaitForAssertion(() => Assert.NotNull(component.Instance.Library));

        Library edited = component.Instance.Library!;
        edited.IngestPaths.RemoveAll(p => p.Id == removedIngestId);
        edited.FilterRules.RemoveAll(f => f.Id == removedFilterId);
        edited.RenameRules.Remove(edited.RenameRules.First(r => r.Id == removedRenameId));

        component.Instance.IsValid = true;
        component.Render(parameters => parameters.Add(p => p.LibraryId, library.Id));

        await component.InvokeAsync(() =>
            component
                .FindAll("button")
                .First(b => b.TextContent.Contains("Save Changes", StringComparison.Ordinal))
                .Click(new MouseEventArgs())
        );

        // The save is async; wait until it has committed and navigated before reading the database.
        component.WaitForAssertion(
            () =>
            {
                var navigation = Services.GetRequiredService<NavigationManager>();
                Assert.EndsWith("/libraries", navigation.Uri);
            },
            TimeSpan.FromSeconds(15)
        );

        await using var db = await _testDb.Database.CreateContextAsync();
        Library persisted = await db
            .Libraries.Include(l => l.IngestPaths)
            .Include(l => l.FilterRules)
            .Include(l => l.RenameRules)
            .FirstAsync(l => l.Id == library.Id);

        Assert.Single(persisted.IngestPaths);
        Assert.DoesNotContain(persisted.IngestPaths, p => p.Id == removedIngestId);
        Assert.Single(persisted.FilterRules);
        Assert.DoesNotContain(persisted.FilterRules, f => f.Id == removedFilterId);
        Assert.Single(persisted.RenameRules);
        Assert.DoesNotContain(persisted.RenameRules, r => r.Id == removedRenameId);
    }

    [Fact]
    public async Task Save_WhenLibraryWasDeletedElsewhere_ShowsErrorAndNavigatesBack()
    {
        Library library = await SeedLibraryAsync();

        var component = RenderWithProviders<EditLibrary>(parameters =>
            parameters.Add(p => p.LibraryId, library.Id)
        );
        component.WaitForAssertion(() => Assert.NotNull(component.Instance.Library));

        // Simulate another tab deleting the row between load and save.
        await using (var db = await _testDb.Database.CreateContextAsync())
        {
            Library tracked = await db.Libraries.FirstAsync(l => l.Id == library.Id);
            db.Libraries.Remove(tracked);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        component.Instance.Library!.Name = "Edit after deletion";
        component.Instance.IsValid = true;
        component.Render(parameters => parameters.Add(p => p.LibraryId, library.Id));

        await component.InvokeAsync(() =>
            component
                .FindAll("button")
                .First(b => b.TextContent.Contains("Save Changes", StringComparison.Ordinal))
                .Click(new MouseEventArgs())
        );

        component.WaitForAssertion(() =>
            _subSnackbar
                .Received(1)
                .Add(
                    Arg.Is<string>(m => m.Contains("Error_LibraryNotFound")),
                    Severity.Error,
                    Arg.Any<Action<SnackbarOptions>?>(),
                    Arg.Any<string?>()
                )
        );

        var navigation = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/libraries", navigation.Uri);

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.Libraries.AnyAsync(l => l.Id == library.Id));
    }

    private async Task<Library> SeedLibraryAsync()
    {
        var library = new Library
        {
            Name = "Original Library",
            NotUpscaledLibraryPath = CreateTempDir("not-upscaled"),
            UpscaledLibraryPath = CreateTempDir("upscaled"),
            IngestPaths =
            [
                new LibraryIngestPath { Path = CreateTempDir("ingest-keep"), SortOrder = 0 },
                new LibraryIngestPath { Path = CreateTempDir("ingest-remove"), SortOrder = 1 },
            ],
            FilterRules =
            [
                new LibraryFilterRule
                {
                    Pattern = "keep-filter",
                    Library = null!,
                    PatternType = LibraryFilterPatternType.Contains,
                    TargetField = LibraryFilterTargetField.MangaTitle,
                    Action = FilterAction.Include,
                },
                new LibraryFilterRule
                {
                    Pattern = "remove-filter",
                    Library = null!,
                    PatternType = LibraryFilterPatternType.Contains,
                    TargetField = LibraryFilterTargetField.FilePath,
                    Action = FilterAction.Exclude,
                },
            ],
            RenameRules = new ObservableCollection<LibraryRenameRule>
            {
                new()
                {
                    Pattern = "keep-rename",
                    PatternType = LibraryRenamePatternType.Contains,
                    TargetField = LibraryRenameTargetField.SeriesTitle,
                    Replacement = "kept",
                },
                new()
                {
                    Pattern = "remove-rename",
                    PatternType = LibraryRenamePatternType.Contains,
                    TargetField = LibraryRenameTargetField.FileName,
                    Replacement = "removed",
                },
            },
        };

        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return library;
    }
}
