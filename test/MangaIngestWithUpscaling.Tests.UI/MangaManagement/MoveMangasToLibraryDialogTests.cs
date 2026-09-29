using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.MangaManagement;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.MangaManagement;
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

public class MoveMangasToLibraryDialogTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;
    private IMangaLibraryMover _subMover = null!;

    public MoveMangasToLibraryDialogTests()
    {
        SetupDatabase();
        SetupMocks();
        RegisterServices();
    }

    private void SetupDatabase()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;
    }

    private void SetupMocks()
    {
        _subMover = Substitute.For<IMangaLibraryMover>();
    }

    private void RegisterServices()
    {
        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subMover);

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

    [Fact]
    public async Task MoveMangasToLibraryDialog_Submit_MovesSelectionIntoChosenLibrary()
    {
        var sourceLibrary = new Library
        {
            Name = "Source",
            NotUpscaledLibraryPath = "/test/source",
            UpscaledLibraryPath = "/test/source-upscaled",
        };
        var targetLibrary = new Library
        {
            Name = "Target",
            NotUpscaledLibraryPath = "/test/target",
            UpscaledLibraryPath = "/test/target-upscaled",
        };
        _dbContext.Libraries.AddRange(sourceLibrary, targetLibrary);
        var manga = new Manga
        {
            PrimaryTitle = "Move Me",
            Library = sourceLibrary,
            LibraryId = sourceLibrary.Id,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var provider = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        });

        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<MoveMangasToLibraryDialog>
        {
            {
                x => x.Mangas,
                new List<Manga> { manga }
            },
        };
        IDialogReference reference = await dialogService.ShowAsync<MoveMangasToLibraryDialog>(
            "Move",
            parameters
        );

        IRenderedComponent<MoveMangasToLibraryDialog> dialog = null!;
        provider.WaitForAssertion(() =>
            dialog = provider.FindComponent<MoveMangasToLibraryDialog>()
        );

        var librarySelect = dialog.FindComponent<MudSelect<Library>>();
        await dialog.InvokeAsync(() =>
            librarySelect.Instance.ValueChanged.InvokeAsync(targetLibrary)
        );

        IElement moveButton = dialog.FindAll("button").First(b => b.TextContent.Trim() == "Move");
        await dialog.InvokeAsync(() => moveButton.Click(new MouseEventArgs()));

        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        DialogResult? result = await resultTask;
        Assert.NotNull(result);
        Assert.False(result!.Canceled);

        _ = _subMover
            .Received(1)
            .MoveMangaAsync(
                Arg.Is<Manga>(m => m.Id == manga.Id),
                Arg.Is<Library>(l => l.Id == targetLibrary.Id),
                Arg.Any<CancellationToken>(),
                Arg.Any<ApplicationDbContext>()
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
