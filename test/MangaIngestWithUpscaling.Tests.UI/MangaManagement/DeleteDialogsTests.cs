using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit.Rendering;
using MangaIngestWithUpscaling.Components.MangaManagement;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.MangaManagement;

/// <summary>
/// Floor tests for the manga/chapter delete dialogs. Each dialog is responsible for reloading its
/// targets as tracked entities in the context it hands to <see cref="IChapterDeletion"/>, and for
/// forwarding the two file-deletion flags the user picked before closing the dialog with Ok.
/// </summary>
public class DeleteDialogsTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;
    private IChapterDeletion _subDeletion = null!;

    public DeleteDialogsTests()
    {
        SetupDatabase();
        RegisterServices();
    }

    private void SetupDatabase()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;
    }

    private void RegisterServices()
    {
        _subDeletion = Substitute.For<IChapterDeletion>();

        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subDeletion);

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

    private IRenderedComponent<ContainerFragment> RenderDialogHost()
    {
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        });
    }

    private async Task<(Library library, Manga manga, Chapter chapter)> SeedMangaWithChapterAsync()
    {
        var library = new Library
        {
            Name = "Library A",
            NotUpscaledLibraryPath = "/test/library-a",
            UpscaledLibraryPath = "/test/library-a-upscaled",
        };
        var manga = new Manga { PrimaryTitle = "Manga A", Library = library };
        var chapter = new Chapter
        {
            FileName = "Chapter 1.cbz",
            RelativePath = "Manga A/Chapter 1.cbz",
            Manga = manga,
            IsUpscaled = true,
        };
        manga.Chapters.Add(chapter);

        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return (library, manga, chapter);
    }

    private static async Task SetCheckBoxesAsync(
        IRenderedComponent<IComponent> dialog,
        bool original,
        bool upscaled
    )
    {
        // The dialog renders the original checkbox first, the upscaled one second.
        var checkBoxes = dialog.FindComponents<MudCheckBox<bool>>();
        Assert.Equal(2, checkBoxes.Count);
        await dialog.InvokeAsync(async () =>
        {
            await checkBoxes[0].Instance.ValueChanged.InvokeAsync(original);
            await checkBoxes[1].Instance.ValueChanged.InvokeAsync(upscaled);
        });
    }

    // ------------------------------------------------------------------ DeleteMangasDialog

    [Fact]
    public async Task DeleteMangasDialog_Submit_DelegatesTrackedMangaWithChosenFlagsAndClosesOk()
    {
        (Library library, Manga manga, Chapter chapter) = await SeedMangaWithChapterAsync();

        EntityState? capturedState = null;
        bool hasLibrary = false;
        bool hasChapters = false;
        _subDeletion
            .When(x =>
                x.DeleteManga(
                    Arg.Any<ApplicationDbContext>(),
                    Arg.Any<Manga>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>()
                )
            )
            .Do(call =>
            {
                ApplicationDbContext db = call.Arg<ApplicationDbContext>();
                Manga tracked = call.Arg<Manga>();
                // Capture the tracking state while the dialog still owns the context: it is
                // disposed as soon as Submit returns.
                capturedState = db.Entry(tracked).State;
                hasLibrary = tracked.Library is not null;
                hasChapters = tracked.Chapters.Count > 0;
            });

        var host = RenderDialogHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DeleteMangasDialog>
        {
            {
                x => x.Mangas,
                new List<Manga> { manga }
            },
        };
        IDialogReference reference = await dialogService.ShowAsync<DeleteMangasDialog>(
            "Delete",
            parameters
        );

        IRenderedComponent<DeleteMangasDialog> dialog = null!;
        host.WaitForAssertion(() => dialog = host.FindComponent<DeleteMangasDialog>());

        await SetCheckBoxesAsync(dialog, original: true, upscaled: false);

        IElement submitButton = dialog
            .FindAll("button")
            .First(b => b.TextContent.Trim() == "Delete");
        await dialog.InvokeAsync(() => submitButton.Click(new MouseEventArgs()));

        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        DialogResult? result = await resultTask;
        Assert.NotNull(result);
        Assert.False(result!.Canceled);

        _subDeletion
            .Received(1)
            .DeleteManga(
                Arg.Any<ApplicationDbContext>(),
                Arg.Is<Manga>(m => m.Id == manga.Id),
                true,
                false
            );

        // The dialog must hand the deletion service a tracked graph, not the no-tracking instance.
        Assert.NotNull(capturedState);
        Assert.NotEqual(EntityState.Detached, capturedState!.Value);
        Assert.True(hasLibrary);
        Assert.True(hasChapters);
    }

    [Fact]
    public async Task DeleteMangasDialog_Cancel_DoesNotDelegate()
    {
        (Library library, Manga manga, Chapter chapter) = await SeedMangaWithChapterAsync();

        var host = RenderDialogHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DeleteMangasDialog>
        {
            {
                x => x.Mangas,
                new List<Manga> { manga }
            },
        };
        IDialogReference reference = await dialogService.ShowAsync<DeleteMangasDialog>(
            "Delete",
            parameters
        );

        IRenderedComponent<DeleteMangasDialog> dialog = null!;
        host.WaitForAssertion(() => dialog = host.FindComponent<DeleteMangasDialog>());

        IElement cancelButton = dialog
            .FindAll("button")
            .First(b => b.TextContent.Trim() == "Cancel");
        await dialog.InvokeAsync(() => cancelButton.Click(new MouseEventArgs()));

        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        DialogResult? result = await resultTask;
        Assert.NotNull(result);
        Assert.True(result!.Canceled);

        _subDeletion
            .DidNotReceive()
            .DeleteManga(
                Arg.Any<ApplicationDbContext>(),
                Arg.Any<Manga>(),
                Arg.Any<bool>(),
                Arg.Any<bool>()
            );
    }

    // ------------------------------------------------------------------ DeleteChaptersDialog

    [Fact]
    public async Task DeleteChaptersDialog_Submit_DelegatesTrackedChapterWithChosenFlagsAndClosesOk()
    {
        (Library library, Manga manga, Chapter chapter) = await SeedMangaWithChapterAsync();

        EntityState? capturedState = null;
        bool chapterHasManga = false;
        bool mangaHasLibrary = false;
        _subDeletion
            .When(x =>
                x.DeleteChapter(
                    Arg.Any<ApplicationDbContext>(),
                    Arg.Any<Chapter>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>()
                )
            )
            .Do(call =>
            {
                ApplicationDbContext db = call.Arg<ApplicationDbContext>();
                Chapter tracked = call.Arg<Chapter>();
                // Capture the tracking state while the dialog still owns the context: it is
                // disposed as soon as Submit returns.
                capturedState = db.Entry(tracked).State;
                chapterHasManga = tracked.Manga is not null;
                mangaHasLibrary = tracked.Manga?.Library is not null;
            });

        var host = RenderDialogHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DeleteChaptersDialog>
        {
            {
                x => x.Chapters,
                new List<Chapter> { chapter }
            },
        };
        IDialogReference reference = await dialogService.ShowAsync<DeleteChaptersDialog>(
            "Delete",
            parameters
        );

        IRenderedComponent<DeleteChaptersDialog> dialog = null!;
        host.WaitForAssertion(() => dialog = host.FindComponent<DeleteChaptersDialog>());

        await SetCheckBoxesAsync(dialog, original: false, upscaled: true);

        IElement submitButton = dialog
            .FindAll("button")
            .First(b => b.TextContent.Trim() == "Delete");
        await dialog.InvokeAsync(() => submitButton.Click(new MouseEventArgs()));

        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        DialogResult? result = await resultTask;
        Assert.NotNull(result);
        Assert.False(result!.Canceled);

        _subDeletion
            .Received(1)
            .DeleteChapter(
                Arg.Any<ApplicationDbContext>(),
                Arg.Is<Chapter>(c => c.Id == chapter.Id),
                false,
                true
            );

        Assert.NotNull(capturedState);
        Assert.NotEqual(EntityState.Detached, capturedState!.Value);
        Assert.True(chapterHasManga);
        Assert.True(mangaHasLibrary);
    }

    [Fact]
    public async Task DeleteChaptersDialog_Cancel_DoesNotDelegate()
    {
        (Library library, Manga manga, Chapter chapter) = await SeedMangaWithChapterAsync();

        var host = RenderDialogHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DeleteChaptersDialog>
        {
            {
                x => x.Chapters,
                new List<Chapter> { chapter }
            },
        };
        IDialogReference reference = await dialogService.ShowAsync<DeleteChaptersDialog>(
            "Delete",
            parameters
        );

        IRenderedComponent<DeleteChaptersDialog> dialog = null!;
        host.WaitForAssertion(() => dialog = host.FindComponent<DeleteChaptersDialog>());

        IElement cancelButton = dialog
            .FindAll("button")
            .First(b => b.TextContent.Trim() == "Cancel");
        await dialog.InvokeAsync(() => cancelButton.Click(new MouseEventArgs()));

        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        DialogResult? result = await resultTask;
        Assert.NotNull(result);
        Assert.True(result!.Canceled);

        _subDeletion
            .DidNotReceive()
            .DeleteChapter(
                Arg.Any<ApplicationDbContext>(),
                Arg.Any<Chapter>(),
                Arg.Any<bool>(),
                Arg.Any<bool>()
            );
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Run the whole teardown on the thread pool: bUnit's service-provider disposal (which
        // disposes the shared ApplicationDbContext) and the database drop both resume async
        // continuations, and resuming them on the renderer's synchronization context can deadlock.
        await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);

        if (_testDb is not null)
        {
            TestDatabaseHelper.TestDbContext testDb = _testDb;
            _testDb = null!;
            await testDb.DisposeAsync().ConfigureAwait(false);
        }
    }
}
