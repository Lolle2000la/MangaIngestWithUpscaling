using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.Libraries;
using MangaIngestWithUpscaling.Components.Libraries.Dialogs;
using MangaIngestWithUpscaling.Components.Libraries.Filters;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
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

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

/// <summary>
/// Interaction tests for the rename-rule dialog and its live preview. The previous tests only
/// asserted that some markup existed; these drive the real preview generation and the rule-change
/// refresh path.
/// </summary>
public class DialogsTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;

    public DialogsTests()
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
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton<ILibraryRenamingService, LibraryRenamingService>();
        Services.AddSingleton(Substitute.For<IChapterInIngestRecognitionService>());
        Services.AddSingleton(Substitute.For<IMetadataHandlingService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

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
    public async Task PreviewLibraryRenames_GeneratesSeriesPreviewFromRules()
    {
        Library library = CreateLibraryWithRenames();

        var preview = Render<PreviewLibraryRenames>(parameters =>
            parameters.Add(p => p.Library, library)
        );

        await ExpandExistingSeriesPanelAsync(preview);

        preview.WaitForAssertion(() => Assert.True(GetSeriesPreviewCount(preview) > 0));
        preview.WaitForAssertion(() =>
            Assert.Contains(
                preview.FindAll("td"),
                td => td.TextContent.Contains("Changed Series", StringComparison.Ordinal)
            )
        );
        Assert.Contains(
            preview.FindAll("td"),
            td => td.TextContent.Contains("Original Series", StringComparison.Ordinal)
        );
    }

    private static int GetSeriesPreviewCount(IRenderedComponent<PreviewLibraryRenames> preview)
    {
        object collection = typeof(PreviewLibraryRenames)
            .GetField(
                "seriesPreviews",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            )!
            .GetValue(preview.Instance)!;
        return (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
    }

    [Fact]
    public async Task LibraryRenameDialog_RemovingARule_RefreshesThePreview()
    {
        Library library = CreateLibraryWithRenames();

        var host = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        });

        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<LibraryRenameDialog>
        {
            { component => component.Library, library },
        };
        IDialogReference reference = await dialogService.ShowAsync<LibraryRenameDialog>(
            "Renames",
            parameters
        );

        IRenderedComponent<LibraryRenameDialog> dialog = null!;
        host.WaitForAssertion(() => dialog = host.FindComponent<LibraryRenameDialog>());
        IRenderedComponent<PreviewLibraryRenames> preview =
            dialog.FindComponent<PreviewLibraryRenames>();
        await ExpandExistingSeriesPanelAsync(preview);
        preview.WaitForAssertion(() =>
            Assert.Contains(
                preview.FindAll("td"),
                td => td.TextContent.Contains("Changed Series", StringComparison.Ordinal)
            )
        );

        // Adding a rule keeps a preview around and grows the collection.
        IRenderedComponent<EditLibraryRenames> editRenames =
            dialog.FindComponent<EditLibraryRenames>();
        IRenderedComponent<MudIconButton> addButton = editRenames
            .FindComponents<MudIconButton>()
            .First(b => b.Instance.Icon == Icons.Material.Filled.Add);
        await editRenames.InvokeAsync(() =>
            addButton.Instance.OnClick.InvokeAsync(new MouseEventArgs())
        );
        Assert.Equal(2, library.RenameRules.Count);

        // Removing the renaming rule must clear the generated preview again.
        IRenderedComponent<MudIconButton> removeButton = editRenames
            .FindComponents<MudIconButton>()
            .First(b => b.Instance.Icon == Icons.Material.Filled.Delete);
        await editRenames.InvokeAsync(() =>
            removeButton.Instance.OnClick.InvokeAsync(new MouseEventArgs())
        );

        Assert.Single(library.RenameRules);
        preview.WaitForAssertion(() =>
            Assert.DoesNotContain(
                preview.FindAll("td"),
                td => td.TextContent.Contains("Changed Series", StringComparison.Ordinal)
            )
        );
    }

    private static Library CreateLibraryWithRenames()
    {
        return new Library
        {
            Id = 1,
            Name = "Test Library",
            MangaSeries = [new Manga { Id = 1, PrimaryTitle = "Original Series" }],
            IngestPaths = [new LibraryIngestPath { Path = "/does/not/exist", SortOrder = 0 }],
            RenameRules = new ObservableCollection<LibraryRenameRule>
            {
                new()
                {
                    Pattern = "Original",
                    PatternType = LibraryRenamePatternType.Contains,
                    TargetField = LibraryRenameTargetField.SeriesTitle,
                    Replacement = "Changed",
                },
            },
        };
    }

    private static async Task ExpandExistingSeriesPanelAsync(
        IRenderedComponent<PreviewLibraryRenames> preview
    )
    {
        IRenderedComponent<MudExpansionPanel> panel = preview
            .FindComponents<MudExpansionPanel>()
            .First(p => p.Instance.Text.Contains("Existing Series", StringComparison.Ordinal));
        await preview.InvokeAsync(() => panel.Instance.ExpandAsync());
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
