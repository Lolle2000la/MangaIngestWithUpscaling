using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.MangaManagement;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MangaManagement;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.MangaManagement;

/// <summary>
/// Regression tests for the "merge selected" flow when the selected table rows came from more than
/// one query (issue #478). The dialog must reload the mangas tracked by id and hand its context to
/// the merger instead of passing the no-tracking, per-query identity-resolved graph.
/// </summary>
public class MergeMangaDialogTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;

    private IChapterChangedNotifier _subChapterChangedNotifier = null!;
    private IFileSystem _subFileSystem = null!;
    private IMangaMetadataChanger _subMetadataChanger = null!;
    private IMetadataHandlingService _subMetadataHandler = null!;

    public MergeMangaDialogTests()
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
        _subChapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        _subFileSystem = Substitute.For<IFileSystem>();
        _subMetadataChanger = Substitute.For<IMangaMetadataChanger>();
        _subMetadataHandler = Substitute.For<IMetadataHandlingService>();
    }

    private void RegisterServices()
    {
        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subChapterChangedNotifier);
        Services.AddSingleton(_subFileSystem);
        Services.AddSingleton(_subMetadataChanger);
        Services.AddSingleton(_subMetadataHandler);
        Services.AddSingleton(Substitute.For<ILogger<MangaMerger>>());

        Services.AddSingleton<IMangaMerger>(sp => new MangaMerger(
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            sp.GetRequiredService<IMetadataHandlingService>(),
            sp.GetRequiredService<IMangaMetadataChanger>(),
            sp.GetRequiredService<ILogger<MangaMerger>>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IChapterChangedNotifier>()
        ));

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
    public async Task MergeMangaDialog_TwoRowsFromDifferentQueries_MergesAndClosesOk()
    {
        var library = new Library
        {
            Name = "Library A",
            NotUpscaledLibraryPath = "/test/library-a",
            UpscaledLibraryPath = "/test/library-a-upscaled",
        };
        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.AddRange(
            new Manga
            {
                PrimaryTitle = "Primary",
                Library = library,
                LibraryId = library.Id,
            },
            new Manga
            {
                PrimaryTitle = "To Merge",
                Library = library,
                LibraryId = library.Id,
            }
        );
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Load each manga graph through its own context, as two MudTable pages did. Identity is only
        // resolved within one query, so the two mangas carry distinct Library instances with the
        // same key: exactly the graph that used to make the merger's graph-attach throw.
        Manga primaryGraph;
        Manga mergedGraph;
        await using (var firstDb = await _testDb.Database.CreateContextAsync())
        {
            primaryGraph = await firstDb
                .MangaSeries.AsNoTrackingWithIdentityResolution()
                .Include(m => m.Library)
                .FirstAsync(m => m.PrimaryTitle == "Primary");
        }
        await using (var secondDb = await _testDb.Database.CreateContextAsync())
        {
            mergedGraph = await secondDb
                .MangaSeries.AsNoTrackingWithIdentityResolution()
                .Include(m => m.Library)
                .FirstAsync(m => m.PrimaryTitle == "To Merge");
        }
        Assert.NotSame(primaryGraph.Library, mergedGraph.Library);

        var provider = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        });

        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<MergeMangaDialog>
        {
            {
                x => x.Mangas,
                new List<Manga> { primaryGraph, mergedGraph }
            },
        };
        IDialogReference reference = await dialogService.ShowAsync<MergeMangaDialog>(
            "Merge",
            parameters
        );

        IRenderedComponent<MergeMangaDialog> dialog = null!;
        provider.WaitForAssertion(() => dialog = provider.FindComponent<MergeMangaDialog>());

        var primarySelect = dialog.FindComponent<MudSelect<Manga>>();
        await dialog.InvokeAsync(() =>
            primarySelect.Instance.ValueChanged.InvokeAsync(primaryGraph)
        );

        IElement mergeButton = dialog.FindAll("button").First(b => b.TextContent.Trim() == "Merge");
        await dialog.InvokeAsync(() => mergeButton.Click(new MouseEventArgs()));

        Task<DialogResult?> resultTask = reference.Result;
        Task completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(resultTask, completed);
        DialogResult? result = await resultTask;
        Assert.NotNull(result);
        Assert.False(result!.Canceled);

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.MangaSeries.AnyAsync(m => m.PrimaryTitle == "To Merge"));
        Assert.True(await verifyDb.MangaAlternativeTitles.AnyAsync(t => t.Title == "To Merge"));
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
