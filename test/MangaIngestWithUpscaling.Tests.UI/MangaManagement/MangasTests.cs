using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.MangaManagement;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MangaManagement;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.MangaManagement;

public class MangasTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;

    private IChapterChangedNotifier _subChapterChangedNotifier = null!;
    private IDialogService _subDialogService = null!;
    private IFileSystem _subFileSystem = null!;
    private IMangaMetadataChanger _subMetadataChanger = null!;
    private IMetadataHandlingService _subMetadataHandler = null!;
    private ISnackbar _subSnackbar = null!;
    private ITaskQueue _subTaskQueue = null!;

    public MangasTests()
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
        _subDialogService = Substitute.For<IDialogService>();
        _subFileSystem = Substitute.For<IFileSystem>();
        _subMetadataChanger = Substitute.For<IMangaMetadataChanger>();
        _subMetadataHandler = Substitute.For<IMetadataHandlingService>();
        _subSnackbar = Substitute.For<ISnackbar>();
        _subTaskQueue = Substitute.For<ITaskQueue>();
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
        Services.AddSingleton(_subDialogService);
        Services.AddSingleton(_subFileSystem);
        Services.AddSingleton(_subMetadataChanger);
        Services.AddSingleton(_subMetadataHandler);
        Services.AddSingleton(_subSnackbar);
        Services.AddSingleton(_subTaskQueue);
        Services.AddSingleton(Substitute.For<ILogger<MangaMerger>>());
        Services.AddSingleton(Substitute.For<ILogger<Mangas>>());

        // Real merger so the merge dialog exercises the actual attach/reload flow.
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

    // ---------------------------------------------------------------- data helpers

    private async Task<UpscalerProfile> AddProfileAsync()
    {
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return profile;
    }

    private async Task<Library> AddLibraryAsync(string name, int? upscalerProfileId = null)
    {
        var library = new Library
        {
            Name = name,
            NotUpscaledLibraryPath = $"/test/{name}",
            UpscaledLibraryPath = $"/test/{name}-upscaled",
            UpscalerProfileId = upscalerProfileId,
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return library;
    }

    private async Task<Manga> AddMangaAsync(
        Library library,
        string title,
        bool withUnupscaledChapter = false
    )
    {
        var manga = new Manga
        {
            PrimaryTitle = title,
            LibraryId = library.Id,
            Library = library,
        };
        if (withUnupscaledChapter)
        {
            manga.Chapters.Add(
                new Chapter
                {
                    FileName = $"{title} 1.cbz",
                    RelativePath = $"{title}/{title} 1.cbz",
                    Manga = manga,
                    IsUpscaled = false,
                }
            );
        }

        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return manga;
    }

    private IRenderedComponent<T> RenderComponentWithProviders<T>(
        Action<ComponentParameterCollectionBuilder<T>>? parameterBuilder = null
    )
        where T : class, IComponent
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
        };
        Render(fragment);

        return parameterBuilder != null ? Render<T>(parameterBuilder) : Render<T>();
    }

    // ---------------------------------------------------------------- inline edit

    [Fact]
    public async Task Mangas_InlineTitleEditCommit_UpdatesRowAndDatabase()
    {
        Library library = await AddLibraryAsync("Library A");
        Manga manga = await AddMangaAsync(library, "Original Title");

        const string updatedTitle = "Renamed Title";
        _subMetadataChanger
            .ChangeMangaTitle(
                Arg.Any<Manga>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<MangaRenameProgress>>(),
                Arg.Any<ApplicationDbContext?>()
            )
            .Returns(callInfo =>
            {
                // The real changer promotes the new title onto the attached manga.
                callInfo.Arg<Manga>().PrimaryTitle = callInfo.ArgAt<string>(1);
                return Task.FromResult(RenameResult.Ok);
            });

        var component = RenderComponentWithProviders<Mangas>();

        component.WaitForAssertion(() =>
            Assert.True(
                FindDataRow(component, "Original Title") is not null,
                "The original title should be rendered in a table row"
            )
        );

        // Clicking the row starts inline editing (row edit trigger defaults to RowClick).
        IElement row = FindDataRow(component, "Original Title")!;
        await row.ClickAsync(new MouseEventArgs());

        component.WaitForAssertion(() =>
            Assert.True(
                component.FindComponents<MudInput<string>>().Count > 0,
                "Inline editing should render a text input"
            )
        );

        IRenderedComponent<MudInput<string>> titleInput = component
            .FindComponents<MudInput<string>>()
            .First(i => i.Instance.GetState(x => x.Value) == "Original Title");
        await component.InvokeAsync(() =>
            titleInput.Instance.ValueChanged.InvokeAsync(updatedTitle)
        );

        IElement commitButton = component
            .FindAll("button")
            .First(b => b.GetAttribute("aria-label")?.Contains("Commit edit") == true);
        await commitButton.ClickAsync(new MouseEventArgs());

        // The commit handler is fire-and-forget; wait for the reloaded row and the database.
        component.WaitForAssertion(() =>
            Assert.True(
                FindDataRow(component, updatedTitle) is not null,
                "The updated title should appear in the row template after committing"
            )
        );

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Manga persisted = await verifyDb.MangaSeries.FirstAsync(m => m.Id == manga.Id);
        Assert.Equal(updatedTitle, persisted.PrimaryTitle);

        _ = _subMetadataChanger
            .Received(1)
            .ChangeMangaTitle(
                Arg.Is<Manga>(m => m.Id == manga.Id),
                updatedTitle,
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<MangaRenameProgress>>(),
                Arg.Any<ApplicationDbContext?>()
            );
    }

    // ---------------------------------------------------------------- upscale specific

    [Fact]
    public async Task Mangas_UpscaleSpecific_EnqueuesUpscaleTaskForUnupscaledChapter()
    {
        UpscalerProfile profile = await AddProfileAsync();
        Library library = await AddLibraryAsync("Library A", profile.Id);
        await AddMangaAsync(library, "Manga A", withUnupscaledChapter: true);

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
            Assert.True(FindDataRow(component, "Manga A") is not null)
        );

        IElement upscaleButton = FindDataRow(component, "Manga A")!
            .QuerySelectorAll("button")
            .First(b => b.GetAttribute("title") == "Upscale");

        await component.InvokeAsync(() => upscaleButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
            _ = _subTaskQueue.Received(1).EnqueueAsync(Arg.Any<UpscaleTask>())
        );

        await _subTaskQueue
            .Received(1)
            .EnqueueAsync(Arg.Is<UpscaleTask>(t => t.UpscalerProfileId == profile.Id));
    }

    // ---------------------------------------------------------------- delete selected

    [Fact]
    public async Task Mangas_DeleteSelected_ShowsDialogAndReloadsRows()
    {
        Library library = await AddLibraryAsync("Library A");
        await AddMangaAsync(library, "Keep Me");
        Manga deleteOne = await AddMangaAsync(library, "Delete One");
        Manga deleteTwo = await AddMangaAsync(library, "Delete Two");

        // The dialog owns the deletion; simulate it so the table reload observes fewer rows.
        _subDialogService
            .ShowAsync<DeleteMangasDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(async callInfo =>
            {
                var parameters = callInfo.Arg<DialogParameters>();
                var mangas = (IEnumerable<Manga>)parameters["Mangas"]!;
                await using (var db = await _testDb.Database.CreateContextAsync())
                {
                    foreach (Manga manga in mangas)
                    {
                        Manga? tracked = await db.MangaSeries.FindAsync(manga.Id);
                        if (tracked is not null)
                        {
                            db.MangaSeries.Remove(tracked);
                        }
                    }
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                var reference = Substitute.For<IDialogReference>();
                reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
                return reference;
            });

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
            Assert.True(FindDataRow(component, "Delete One") is not null)
        );

        var table = component.FindComponent<MudTable<Manga>>();
        await component.InvokeAsync(() =>
            table.Instance.SelectedItemsChanged.InvokeAsync(
                new HashSet<Manga> { deleteOne, deleteTwo }
            )
        );

        IElement deleteButton = FindToolbarButton(component, "Delete Selected");
        await component.InvokeAsync(() => deleteButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
        {
            Assert.True(FindDataRow(component, "Delete One") is null);
            Assert.True(FindDataRow(component, "Delete Two") is null);
            Assert.True(FindDataRow(component, "Keep Me") is not null);
        });

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.Equal(1, await verifyDb.MangaSeries.CountAsync());
        Assert.True(await verifyDb.MangaSeries.AnyAsync(m => m.PrimaryTitle == "Keep Me"));
    }

    // ---------------------------------------------------------------- move selected

    [Fact]
    public async Task Mangas_MoveSelected_ShowsMoveDialogWithSelection()
    {
        Library library = await AddLibraryAsync("Library A");
        Manga moved = await AddMangaAsync(library, "Move Me");
        await AddMangaAsync(library, "Stay Here");

        DialogParameters? capturedParameters = null;
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _subDialogService
            .ShowAsync<MoveMangasToLibraryDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(callInfo =>
            {
                capturedParameters = callInfo.Arg<DialogParameters>();
                return Task.FromResult(reference);
            });

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
            Assert.True(FindDataRow(component, "Move Me") is not null)
        );

        var table = component.FindComponent<MudTable<Manga>>();
        await component.InvokeAsync(() =>
            table.Instance.SelectedItemsChanged.InvokeAsync(new HashSet<Manga> { moved })
        );

        IElement moveButton = FindToolbarButton(component, "Move Selected");
        await component.InvokeAsync(() => moveButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() => Assert.NotNull(capturedParameters));

        var mangas = (IEnumerable<Manga>)capturedParameters!["Mangas"]!;
        Assert.Equal(new[] { moved.Id }, mangas.Select(m => m.Id).ToArray());
    }

    // ---------------------------------------------------------------- selection pruning

    [Fact]
    public async Task Mangas_DeleteSelected_PrunesSelectionAndDisablesToolbar()
    {
        Library library = await AddLibraryAsync("Library A");
        await AddMangaAsync(library, "Keep Me");
        Manga deleteOne = await AddMangaAsync(library, "Delete One");
        Manga deleteTwo = await AddMangaAsync(library, "Delete Two");

        _subDialogService
            .ShowAsync<DeleteMangasDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(async callInfo =>
            {
                var parameters = callInfo.Arg<DialogParameters>();
                var mangas = (IEnumerable<Manga>)parameters["Mangas"]!;
                await using (var db = await _testDb.Database.CreateContextAsync())
                {
                    foreach (Manga manga in mangas)
                    {
                        Manga? tracked = await db.MangaSeries.FindAsync(manga.Id);
                        if (tracked is not null)
                        {
                            db.MangaSeries.Remove(tracked);
                        }
                    }
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                var reference = Substitute.For<IDialogReference>();
                reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
                return reference;
            });

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
            Assert.True(FindDataRow(component, "Delete One") is not null)
        );

        var table = component.FindComponent<MudTable<Manga>>();
        await component.InvokeAsync(() =>
            table.Instance.SelectedItemsChanged.InvokeAsync(
                new HashSet<Manga> { deleteOne, deleteTwo }
            )
        );

        IElement deleteButton = FindToolbarButton(component, "Delete Selected");
        await component.InvokeAsync(() => deleteButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
        {
            Assert.True(FindDataRow(component, "Delete One") is null);
            Assert.True(FindDataRow(component, "Delete Two") is null);
        });

        // Nothing is selected any more, so the destructive toolbar must be disabled again rather
        // than keep acting on the vanished rows.
        component.WaitForAssertion(() =>
            Assert.True(
                FindToolbarButton(component, "Delete Selected").HasAttribute("disabled"),
                "The delete toolbar should be disabled once the deleted rows are pruned"
            )
        );
    }

    [Fact]
    public async Task Mangas_MergeSelected_PrunesSelectionAndReloadsRows()
    {
        Library library = await AddLibraryAsync("Library A");
        Manga survivor = await AddMangaAsync(library, "Merge A");
        Manga absorbed = await AddMangaAsync(library, "Merge B");

        // The real dialog merges the selection into one manga; simulate that by dropping the
        // absorbed rows and completing with Ok.
        _subDialogService
            .ShowAsync<MergeMangaDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(async callInfo =>
            {
                var parameters = callInfo.Arg<DialogParameters>();
                var mangas = (IEnumerable<Manga>)parameters["Mangas"]!;
                await using (var db = await _testDb.Database.CreateContextAsync())
                {
                    foreach (Manga manga in mangas.Where(m => m.Id != survivor.Id))
                    {
                        Manga? tracked = await db.MangaSeries.FindAsync(manga.Id);
                        if (tracked is not null)
                        {
                            db.MangaSeries.Remove(tracked);
                        }
                    }
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                var reference = Substitute.For<IDialogReference>();
                reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
                return reference;
            });

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
            Assert.True(FindDataRow(component, "Merge B") is not null)
        );

        var table = component.FindComponent<MudTable<Manga>>();
        await component.InvokeAsync(() =>
            table.Instance.SelectedItemsChanged.InvokeAsync(
                new HashSet<Manga> { survivor, absorbed }
            )
        );

        IElement mergeButton = FindToolbarButton(component, "Merge Selected");
        await component.InvokeAsync(() => mergeButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
        {
            Assert.True(FindDataRow(component, "Merge B") is null);
            Assert.True(FindDataRow(component, "Merge A") is not null);
        });

        component.WaitForAssertion(() =>
            Assert.True(
                FindToolbarButton(component, "Merge Selected").HasAttribute("disabled"),
                "The merge toolbar should be disabled once the merged rows are pruned"
            )
        );
    }

    [Fact]
    public async Task Mangas_MoveSelected_PrunesSelectionAndReloadsFilteredRows()
    {
        Library source = await AddLibraryAsync("Library A");
        Library target = await AddLibraryAsync("Library B");
        Manga moved = await AddMangaAsync(source, "Move Me");
        await AddMangaAsync(source, "Stay Here");

        _subDialogService
            .ShowAsync<MoveMangasToLibraryDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(async callInfo =>
            {
                var parameters = callInfo.Arg<DialogParameters>();
                var mangas = (IEnumerable<Manga>)parameters["Mangas"]!;
                await using (var db = await _testDb.Database.CreateContextAsync())
                {
                    foreach (Manga manga in mangas)
                    {
                        Manga? tracked = await db.MangaSeries.FindAsync(manga.Id);
                        if (tracked is not null)
                        {
                            tracked.LibraryId = target.Id;
                        }
                    }
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                var reference = Substitute.For<IDialogReference>();
                reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
                return reference;
            });

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
        {
            Assert.True(FindDataRow(component, "Move Me") is not null);
            Assert.True(FindDataRow(component, "Stay Here") is not null);
        });

        // Filter to the source library so a moved row leaves the reloaded table.
        var librarySelect = component.FindComponent<MudSelect<Library?>>();
        await component.InvokeAsync(() => librarySelect.Instance.ValueChanged.InvokeAsync(source));

        var table = component.FindComponent<MudTable<Manga>>();
        await component.InvokeAsync(() =>
            table.Instance.SelectedItemsChanged.InvokeAsync(new HashSet<Manga> { moved })
        );

        IElement moveButton = FindToolbarButton(component, "Move Selected");
        await component.InvokeAsync(() => moveButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
        {
            Assert.True(
                FindDataRow(component, "Move Me") is null,
                "The moved manga should leave the source-library table"
            );
            Assert.True(FindDataRow(component, "Stay Here") is not null);
        });

        component.WaitForAssertion(() =>
            Assert.True(
                FindToolbarButton(component, "Move Selected").HasAttribute("disabled"),
                "The move toolbar should be disabled once the moved rows are pruned"
            )
        );
    }

    // ---------------------------------------------------------------- library filter

    [Fact]
    public async Task Mangas_LibraryFilter_ReloadsFilteredRows()
    {
        Library first = await AddLibraryAsync("Library A");
        Library second = await AddLibraryAsync("Library B");
        await AddMangaAsync(first, "In First");
        await AddMangaAsync(second, "In Second");

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
        {
            Assert.True(FindDataRow(component, "In First") is not null);
            Assert.True(FindDataRow(component, "In Second") is not null);
        });

        var librarySelect = component.FindComponent<MudSelect<Library?>>();
        await component.InvokeAsync(() => librarySelect.Instance.ValueChanged.InvokeAsync(first));

        component.WaitForAssertion(() =>
        {
            Assert.True(FindDataRow(component, "In First") is not null);
            Assert.True(FindDataRow(component, "In Second") is null);
        });
    }

    // ---------------------------------------------------------------- edit link

    [Fact]
    public async Task Mangas_EditAction_LinksToEditPage()
    {
        Library library = await AddLibraryAsync("Library A");
        Manga manga = await AddMangaAsync(library, "Link Me");

        var component = RenderComponentWithProviders<Mangas>();
        component.WaitForAssertion(() =>
            Assert.True(FindDataRow(component, "Link Me") is not null)
        );

        IElement anchor = FindDataRow(component, "Link Me")!.QuerySelector("a")!;
        Assert.Equal($"mangas/{manga.Id}", anchor.GetAttribute("href"));
    }

    // ---------------------------------------------------------------- helpers

    private static IElement? FindDataRow<T>(IRenderedComponent<T> component, string title)
        where T : class, IComponent
    {
        return component
            .FindAll("tr")
            .FirstOrDefault(row =>
                row.QuerySelectorAll("td").Any() && row.TextContent.Contains(title)
            );
    }

    private static IElement FindToolbarButton<T>(IRenderedComponent<T> component, string text)
        where T : class, IComponent
    {
        return component
            .FindAll("button")
            .First(b => b.TextContent.Contains(text, StringComparison.Ordinal));
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
