using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit.Rendering;
using MangaIngestWithUpscaling.Components.Libraries.FilteredImages;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

/// <summary>
/// Interaction tests for <see cref="AddImageFilterDialog"/>. The C1 regression is the argument
/// alignment: the description used to be passed as the MIME type (4th argument), which left the
/// real description unset and produced a bogus <c>data:</c> URL for the thumbnail.
/// </summary>
public class AddImageFilterDialogTests : LibraryTestBase
{
    [Fact]
    public async Task Add_FromUploadedFile_PassesDescriptionAsDescriptionAndPersistsTheFilter()
    {
        Library library = await SeedLibraryAsync();
        ConfigureFilterService();

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IDialogReference reference = await ShowAddDialogAsync(host, library);
        IRenderedComponent<AddImageFilterDialog> dialog = FindDialog<AddImageFilterDialog>(host);

        await SelectFileAsync(
            dialog,
            new FakeBrowserFile("blocked-page.png", new byte[] { 1, 2, 3, 4 })
        );
        await SetDescriptionAsync(dialog, "cover page");

        await ClickDialogButtonAsync(dialog, "Add Filter");
        DialogResult? result = await AwaitResultAsync(reference);

        Assert.NotNull(result);
        Assert.False(result!.Canceled);

        // The regression: the description must be the 5th argument and mimeType must stay null.
        await _subImageFilterService
            .Received(1)
            .CreateFilteredImageFromBytesAsync(
                Arg.Any<byte[]>(),
                "blocked-page.png",
                Arg.Any<Library>(),
                Arg.Is<string?>(mimeType => mimeType == null),
                "cover page"
            );

        await using var db = await _testDb.Database.CreateContextAsync();
        FilteredImage persisted = await db.FilteredImages.SingleAsync(f =>
            f.LibraryId == library.Id
        );
        Assert.Equal("blocked-page.png", persisted.OriginalFileName);
        Assert.Equal("cover page", persisted.Description);

        // The detached library must not be re-inserted alongside the new filter.
        Assert.Equal(1, await db.Libraries.CountAsync());
    }

    [Fact]
    public async Task Add_WhenServiceReturnsNoImage_ClosesCanceledAndPersistsNothing()
    {
        Library library = await SeedLibraryAsync();
        ConfigureFilterService();
        _subImageFilterService
            .CreateFilteredImageFromBytesAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<Library>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(Task.FromResult<FilteredImage>(null!));

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IDialogReference reference = await ShowAddDialogAsync(host, library);
        IRenderedComponent<AddImageFilterDialog> dialog = FindDialog<AddImageFilterDialog>(host);

        await SelectFileAsync(
            dialog,
            new FakeBrowserFile("blocked-page.png", new byte[] { 1, 2, 3, 4 })
        );

        await ClickDialogButtonAsync(dialog, "Add Filter");
        DialogResult? result = await AwaitResultAsync(reference);

        Assert.NotNull(result);
        Assert.True(result!.Canceled);

        await using var db = await _testDb.Database.CreateContextAsync();
        Assert.Empty(db.FilteredImages);
    }

    private async Task<Library> SeedLibraryAsync()
    {
        var library = new Library
        {
            Name = "Filters",
            NotUpscaledLibraryPath = CreateTempDir("not-upscaled"),
            UpscaledLibraryPath = CreateTempDir("upscaled"),
            IngestPaths = [new LibraryIngestPath { Path = CreateTempDir("ingest") }],
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return library;
    }

    private void ConfigureFilterService()
    {
        _subImageFilterService
            .GenerateThumbnailBase64Async(Arg.Any<byte[]>(), Arg.Any<int>())
            .Returns("dGh1bWI=");
        _subImageFilterService.CalculateContentHash(Arg.Any<byte[]>()).Returns("content-hash");
        _subImageFilterService
            .CreateFilteredImageFromBytesAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<Library>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(callInfo => new FilteredImage
            {
                Library = null!,
                LibraryId = callInfo.Arg<Library>().Id,
                OriginalFileName = callInfo.ArgAt<string>(1),
                MimeType = callInfo.ArgAt<string?>(3),
                Description = callInfo.ArgAt<string?>(4),
                ContentHash = "content-hash",
            });
    }

    private async Task<IDialogReference> ShowAddDialogAsync(
        IRenderedComponent<ContainerFragment> host,
        Library library
    )
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<AddImageFilterDialog>
        {
            { component => component.Library, library },
        };
        return await dialogService.ShowAsync<AddImageFilterDialog>("Add Filter", parameters);
    }

    internal static async Task SelectFileAsync(
        IRenderedComponent<AddImageFilterDialog> dialog,
        IBrowserFile file
    )
    {
        var upload = dialog.FindComponent<MudFileUpload<IBrowserFile>>();
        await dialog.InvokeAsync(() => upload.Instance.FilesChanged.InvokeAsync(file));
    }

    internal static async Task SetDescriptionAsync(
        IRenderedComponent<AddImageFilterDialog> dialog,
        string description
    )
    {
        var field = dialog
            .FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == "Description");
        await dialog.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(description));
    }

    internal static async Task ClickDialogButtonAsync<TComponent>(
        IRenderedComponent<TComponent> dialog,
        string text
    )
        where TComponent : class, IComponent
    {
        IElement button = dialog
            .FindAll("button")
            .First(b => b.TextContent.Contains(text, StringComparison.Ordinal));
        await dialog.InvokeAsync(() => button.Click(new MouseEventArgs()));
    }

    internal static IRenderedComponent<TDialog> FindDialog<TDialog>(
        IRenderedComponent<ContainerFragment> host
    )
        where TDialog : class, IComponent
    {
        IRenderedComponent<TDialog> dialog = null!;
        host.WaitForAssertion(() => dialog = host.FindComponent<TDialog>());
        return dialog;
    }

    internal static async Task<DialogResult?> AwaitResultAsync(IDialogReference reference)
    {
        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        return await resultTask;
    }
}

/// <summary>
/// Interaction tests for <see cref="EditImageFilterDialog"/>. The C4 regression attached the whole
/// detached graph and silently swallowed failures, leaving the UI showing an edit that was never
/// saved.
/// </summary>
public class EditImageFilterDialogTests : LibraryTestBase
{
    [Fact]
    public async Task Save_UpdatesOnlyTheDescriptionAndClosesOk()
    {
        (Library library, FilteredImage detached) = await SeedFilteredImageAsync();

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IDialogReference reference = await ShowEditDialogAsync(host, detached);
        IRenderedComponent<EditImageFilterDialog> dialog =
            AddImageFilterDialogTests.FindDialog<EditImageFilterDialog>(host);

        dialog.Instance.FilteredImage.Description = "updated description";
        await AddImageFilterDialogTests.ClickDialogButtonAsync(dialog, "Save");
        DialogResult? result = await AddImageFilterDialogTests.AwaitResultAsync(reference);

        Assert.NotNull(result);
        Assert.False(result!.Canceled);

        await using var db = await _testDb.Database.CreateContextAsync();
        FilteredImage persisted = await db.FilteredImages.SingleAsync(f => f.Id == detached.Id);
        Assert.Equal("updated description", persisted.Description);

        // Attaching the detached graph would have dragged the sibling image along and could
        // duplicate the library; the sibling must stay untouched and no library row may be added.
        FilteredImage sibling = await db.FilteredImages.SingleAsync(f =>
            f.Id != detached.Id && f.LibraryId == library.Id
        );
        Assert.Equal("sibling description", sibling.Description);
        Assert.Equal(2, await db.FilteredImages.CountAsync());
        Assert.Equal(1, await db.Libraries.CountAsync());
    }

    [Fact]
    public async Task Save_WhenRowDisappeared_RestoresDescriptionAndClosesCanceled()
    {
        (Library library, FilteredImage detached) = await SeedFilteredImageAsync();

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IDialogReference reference = await ShowEditDialogAsync(host, detached);
        IRenderedComponent<EditImageFilterDialog> dialog =
            AddImageFilterDialogTests.FindDialog<EditImageFilterDialog>(host);

        // The row is deleted in another tab between opening the dialog and saving.
        await using (var db = await _testDb.Database.CreateContextAsync())
        {
            FilteredImage tracked = await db.FilteredImages.FirstAsync(f => f.Id == detached.Id);
            db.FilteredImages.Remove(tracked);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        detached.Description = "cannot be saved";
        await AddImageFilterDialogTests.ClickDialogButtonAsync(dialog, "Save");
        DialogResult? result = await AddImageFilterDialogTests.AwaitResultAsync(reference);

        Assert.NotNull(result);
        Assert.True(result!.Canceled);

        // The in-memory instance must not keep showing the edit that was never persisted.
        Assert.Equal("original description", detached.Description);

        _subSnackbar
            .Received(1)
            .Add(
                Arg.Is<string>(m => m.Contains("Error_SaveFailed")),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string?>()
            );
    }

    private async Task<(Library library, FilteredImage detached)> SeedFilteredImageAsync()
    {
        var library = new Library
        {
            Name = "Filters",
            NotUpscaledLibraryPath = CreateTempDir("not-upscaled"),
            UpscaledLibraryPath = CreateTempDir("upscaled"),
            IngestPaths = [new LibraryIngestPath { Path = CreateTempDir("ingest") }],
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var image = new FilteredImage
        {
            Library = null!,
            LibraryId = library.Id,
            OriginalFileName = "page.png",
            Description = "original description",
            ContentHash = "hash",
            MimeType = "image/png",
        };
        var sibling = new FilteredImage
        {
            Library = null!,
            LibraryId = library.Id,
            OriginalFileName = "sibling.png",
            Description = "sibling description",
            ContentHash = "sibling-hash",
            MimeType = "image/png",
        };
        _dbContext.FilteredImages.AddRange(image, sibling);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Load the detached graph the way the FilteredImages page holds it: the image carries its
        // owning library, and that library exposes its (sibling) filters. EF forbids this cycle in a
        // single no-tracking query, so wire it up after loading.
        await using var db = await _testDb.Database.CreateContextAsync();
        FilteredImage detached = await db
            .FilteredImages.AsNoTracking()
            .Include(f => f.Library)
            .FirstAsync(f => f.Id == image.Id);
        List<FilteredImage> siblings = await db
            .FilteredImages.AsNoTracking()
            .Where(f => f.LibraryId == library.Id)
            .ToListAsync();
        detached.Library.FilteredImages = siblings;
        return (library, detached);
    }

    private async Task<IDialogReference> ShowEditDialogAsync(
        IRenderedComponent<ContainerFragment> host,
        FilteredImage filteredImage
    )
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<EditImageFilterDialog>
        {
            { component => component.FilteredImage, filteredImage },
        };
        return await dialogService.ShowAsync<EditImageFilterDialog>("Edit Filter", parameters);
    }
}

/// <summary>
/// Page-level interaction tests for <see cref="FilteredImages"/>: the add/edit/delete flows must
/// reach the database and refresh the table.
/// </summary>
public class FilteredImagesPageTests : LibraryTestBase
{
    public FilteredImagesPageTests()
    {
        Services.AddSingleton(Substitute.For<ITaskQueue>());
    }

    [Fact]
    public async Task Add_ThroughDialog_ReloadsAndShowsTheNewFilter()
    {
        Library library = await SeedPageAsync();
        ConfigureFilterService();

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IRenderedComponent<FilteredImages> page = Render<FilteredImages>(parameters =>
            parameters.Add(p => p.LibraryId, library.Id)
        );
        page.WaitForAssertion(() => Assert.NotNull(FindRow(page, "existing.png")));

        await ClickPageButtonAsync(page, "Add Filter");
        IRenderedComponent<AddImageFilterDialog> dialog =
            AddImageFilterDialogTests.FindDialog<AddImageFilterDialog>(host);
        await AddImageFilterDialogTests.SelectFileAsync(
            dialog,
            new FakeBrowserFile("new-filter.png", new byte[] { 9, 9, 9 })
        );
        await AddImageFilterDialogTests.SetDescriptionAsync(dialog, "added from page");
        await AddImageFilterDialogTests.ClickDialogButtonAsync(dialog, "Add Filter");

        page.WaitForAssertion(() => Assert.NotNull(FindRow(page, "new-filter.png")));

        await using var db = await _testDb.Database.CreateContextAsync();
        Assert.Equal(2, await db.FilteredImages.CountAsync(f => f.LibraryId == library.Id));
        _subSnackbar
            .Received(1)
            .Add(
                Arg.Is<string>(m => m.Contains("Snackbar_FilterAdded")),
                Severity.Success,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string?>()
            );
    }

    [Fact]
    public async Task Edit_ThroughDialog_UpdatesTheRowAndTheDatabase()
    {
        Library library = await SeedPageAsync();

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IRenderedComponent<FilteredImages> page = Render<FilteredImages>(parameters =>
            parameters.Add(p => p.LibraryId, library.Id)
        );
        page.WaitForAssertion(() => Assert.NotNull(FindRow(page, "existing.png")));

        await ClickRowButtonAsync(page, "existing.png", buttonIndex: 0);
        IRenderedComponent<EditImageFilterDialog> dialog =
            AddImageFilterDialogTests.FindDialog<EditImageFilterDialog>(host);
        dialog.Instance.FilteredImage.Description = "edited from page";
        await AddImageFilterDialogTests.ClickDialogButtonAsync(dialog, "Save");

        page.WaitForAssertion(() =>
        {
            IElement? row = FindRow(page, "existing.png");
            Assert.NotNull(row);
            Assert.Contains("edited from page", row!.TextContent);
        });

        await using var db = await _testDb.Database.CreateContextAsync();
        FilteredImage persisted = await db.FilteredImages.SingleAsync(f =>
            f.LibraryId == library.Id
        );
        Assert.Equal("edited from page", persisted.Description);
        // The snackbar is raised after the async reload; wait for it instead of racing it.
        page.WaitForAssertion(
            () =>
                _subSnackbar
                    .Received(1)
                    .Add(
                        Arg.Is<string>(m => m.Contains("Snackbar_FilterUpdated")),
                        Severity.Success,
                        Arg.Any<Action<SnackbarOptions>?>(),
                        Arg.Any<string?>()
                    ),
            TimeSpan.FromSeconds(15)
        );
    }

    [Fact]
    public async Task Delete_AfterConfirmation_RemovesTheRowAndTheDatabaseEntry()
    {
        Library library = await SeedPageAsync();

        IRenderedComponent<ContainerFragment> host = RenderDialogHost();
        IRenderedComponent<FilteredImages> page = Render<FilteredImages>(parameters =>
            parameters.Add(p => p.LibraryId, library.Id)
        );
        page.WaitForAssertion(() => Assert.NotNull(FindRow(page, "existing.png")));

        await ClickRowButtonAsync(page, "existing.png", buttonIndex: 1);

        // The confirmation is a real MudBlazor message box; click its affirmative button.
        IRenderedComponent<ContainerFragment> confirmHost = host;
        confirmHost.WaitForAssertion(() =>
            Assert.Contains(confirmHost.FindAll("button"), b => b.TextContent.Trim() == "Delete")
        );
        IElement yesButton = confirmHost
            .FindAll("button")
            .First(b => b.TextContent.Trim() == "Delete");
        await confirmHost.InvokeAsync(() => yesButton.Click(new MouseEventArgs()));

        page.WaitForAssertion(() => Assert.Null(FindRow(page, "existing.png")));

        await using var db = await _testDb.Database.CreateContextAsync();
        Assert.Empty(db.FilteredImages);
        page.WaitForAssertion(
            () =>
                _subSnackbar
                    .Received(1)
                    .Add(
                        Arg.Is<string>(m => m.Contains("Snackbar_FilterDeleted")),
                        Severity.Success,
                        Arg.Any<Action<SnackbarOptions>?>(),
                        Arg.Any<string?>()
                    ),
            TimeSpan.FromSeconds(15)
        );
    }

    private async Task<Library> SeedPageAsync()
    {
        var library = new Library
        {
            Name = "Filters",
            NotUpscaledLibraryPath = CreateTempDir("not-upscaled"),
            UpscaledLibraryPath = CreateTempDir("upscaled"),
            IngestPaths = [new LibraryIngestPath { Path = CreateTempDir("ingest") }],
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        _dbContext.FilteredImages.Add(
            new FilteredImage
            {
                Library = null!,
                LibraryId = library.Id,
                OriginalFileName = "existing.png",
                Description = "original description",
                ContentHash = "existing-hash",
                MimeType = "image/png",
                ThumbnailBase64 = "dGh1bWI=",
            }
        );
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return library;
    }

    private void ConfigureFilterService()
    {
        _subImageFilterService
            .GenerateThumbnailBase64Async(Arg.Any<byte[]>(), Arg.Any<int>())
            .Returns("dGh1bWI=");
        _subImageFilterService.CalculateContentHash(Arg.Any<byte[]>()).Returns("content-hash");
        _subImageFilterService
            .CreateFilteredImageFromBytesAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<Library>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(callInfo => new FilteredImage
            {
                Library = null!,
                LibraryId = callInfo.Arg<Library>().Id,
                OriginalFileName = callInfo.ArgAt<string>(1),
                MimeType = callInfo.ArgAt<string?>(3),
                Description = callInfo.ArgAt<string?>(4),
                ContentHash = "content-hash",
            });
    }

    private static IElement? FindRow(IRenderedComponent<FilteredImages> page, string text)
    {
        return page.FindAll("tr")
            .FirstOrDefault(tr => tr.QuerySelectorAll("td").Any() && tr.TextContent.Contains(text));
    }

    private static async Task ClickPageButtonAsync(
        IRenderedComponent<FilteredImages> page,
        string text
    )
    {
        IElement button = page.FindAll("button")
            .First(b => b.TextContent.Contains(text, StringComparison.Ordinal));
        await page.InvokeAsync(() => button.Click(new MouseEventArgs()));
    }

    private static async Task ClickRowButtonAsync(
        IRenderedComponent<FilteredImages> page,
        string rowText,
        int buttonIndex
    )
    {
        IElement row = FindRow(page, rowText)!;
        IElement button = row.QuerySelectorAll("button")[buttonIndex];
        await page.InvokeAsync(() => button.Click(new MouseEventArgs()));
    }
}
