using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.MangaManagement;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using MangaIngestWithUpscaling.Tests.Services.Analysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
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

public class EditMangaTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;

    private IChapterChangedNotifier _subChapterChangedNotifier = null!;
    private IDialogService _subDialogService = null!;
    private IFileSystem _subFileSystem = null!;
    private ILibraryIntegrityChecker _subLibraryIntegrityChecker = null!;
    private IChapterMergeCoordinator _subMergeCoordinator = null!;
    private IMetadataHandlingService _subMetadataHandler = null!;
    private IChapterMergeRevertService _subRevertService = null!;
    private ISnackbar _subSnackbar = null!;
    private ISplitApplicationService _subSplitApplicationService = null!;
    private ISplitProcessingService _subSplitProcessingService = null!;
    private ISplitProcessingCoordinator _subSplitProcessingCoordinator = null!;
    private ISplitProcessingStateManager _subSplitStateManager = null!;
    private IManualSplitService _subManualSplitService = null!;
    private ITaskQueue _subTaskQueue = null!;
    private IWebHostEnvironment _subWebHostEnvironment = null!;

    public EditMangaTests()
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
        _subLibraryIntegrityChecker = Substitute.For<ILibraryIntegrityChecker>();
        _subMergeCoordinator = Substitute.For<IChapterMergeCoordinator>();
        _subMetadataHandler = Substitute.For<IMetadataHandlingService>();
        _subRevertService = Substitute.For<IChapterMergeRevertService>();
        _subSnackbar = Substitute.For<ISnackbar>();
        _subSplitApplicationService = Substitute.For<ISplitApplicationService>();
        _subSplitProcessingService = Substitute.For<ISplitProcessingService>();
        _subSplitProcessingCoordinator = Substitute.For<ISplitProcessingCoordinator>();
        _subSplitStateManager = Substitute.For<ISplitProcessingStateManager>();
        _subManualSplitService = new MockManualSplitService();
        _subTaskQueue = Substitute.For<ITaskQueue>();
        _subWebHostEnvironment = Substitute.For<IWebHostEnvironment>();
        _subWebHostEnvironment.EnvironmentName.Returns("Test");

#pragma warning disable xUnit1051 // Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken
        _subMergeCoordinator
            .GetPossibleMergeActionsAsync(Arg.Any<List<Chapter>>(), Arg.Any<bool>())
            .Returns(new MergeActionInfo());
#pragma warning restore xUnit1051
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
        Services.AddSingleton(_subLibraryIntegrityChecker);
        Services.AddSingleton(_subMergeCoordinator);
        Services.AddSingleton(_subMetadataHandler);
        Services.AddSingleton(_subRevertService);
        Services.AddSingleton(_subSnackbar);
        Services.AddSingleton(_subSplitApplicationService);
        Services.AddSingleton(_subSplitProcessingService);
        Services.AddSingleton(_subSplitProcessingCoordinator);
        Services.AddSingleton(_subSplitStateManager);
        Services.AddSingleton(_subManualSplitService);
        Services.AddSingleton(_subTaskQueue);
        Services.AddSingleton(_subWebHostEnvironment);
        Services.AddSingleton(Substitute.For<ILogger<MangaMetadataChanger>>());
        Services.AddSingleton(Substitute.For<ILogger<EditManga>>());

        // Real changer so the title-change attach/OtherTitles reload path runs.
        Services.AddSingleton<IMangaMetadataChanger, MangaMetadataChanger>();

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

    private async Task<Manga> SeedMangaAsync(
        string primaryTitle,
        IEnumerable<string>? otherTitles = null,
        int? upscalerProfileId = null
    )
    {
        var library = new Library
        {
            Name = "Library A",
            NotUpscaledLibraryPath = "/test/library-a",
            UpscaledLibraryPath = "/test/library-a-upscaled",
        };
        _dbContext.Libraries.Add(library);

        var manga = new Manga
        {
            PrimaryTitle = primaryTitle,
            Library = library,
            LibraryId = library.Id,
            UpscalerProfilePreferenceId = upscalerProfileId,
        };
        if (otherTitles is not null)
        {
            foreach (string title in otherTitles)
            {
                manga.OtherTitles.Add(new MangaAlternativeTitle { Title = title, Manga = manga });
            }
        }

        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return manga;
    }

    private IRenderedComponent<T> RenderWithProviders<T>(
        Action<ComponentParameterCollectionBuilder<T>>? parameterBuilder = null
    )
        where T : class, IComponent
    {
        Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
        });

        return parameterBuilder != null ? Render<T>(parameterBuilder) : Render<T>();
    }

    private async Task SubmitValidFormAsync(IRenderedComponent<EditManga> component, MudForm form)
    {
        await component.InvokeAsync(() => form.ValidateAsync());
        IElement saveButton = component
            .FindAll("button")
            .First(b => b.TextContent.Contains("Save Changes", StringComparison.Ordinal));
        Assert.False(
            saveButton.HasAttribute("disabled"),
            "Save should be enabled once the form is valid and touched"
        );
        await component.InvokeAsync(() => saveButton.Click(new MouseEventArgs()));
    }

    // ---------------------------------------------------------------- Fix 2 regression

    [Fact]
    public async Task EditManga_SaveAlternativeTitles_KeepsRemovedTitleRemoved()
    {
        // Primary A with alternatives [B, C]. The user removes C and renames the primary to D.
        Manga manga = await SeedMangaAsync("A", new[] { "B", "C" });

        var component = RenderWithProviders<EditManga>(parameters =>
            parameters.Add(p => p.MangaId, manga.Id)
        );
        component.WaitForAssertion(() =>
            Assert.Contains(
                component.FindComponents<MudInput<string>>(),
                i => i.Instance.GetState(x => x.Value) == "A"
            )
        );

        // Rename the primary title to D while the basic information tab is active.
        IRenderedComponent<MudInput<string>> titleInput = component
            .FindComponents<MudInput<string>>()
            .First(i => i.Instance.GetState(x => x.Value) == "A");
        await component.InvokeAsync(() => titleInput.Instance.ValueChanged.InvokeAsync("D"));

        // Switch to the alternative titles tab: MudTabs only materializes the active panel.
        IElement altTitlesTab = component
            .FindAll("[role='tab']")
            .First(t => t.TextContent.Contains("Tab_AltTitles", StringComparison.Ordinal));
        await component.InvokeAsync(() => altTitlesTab.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
            Assert.Contains(
                component.FindComponents<MudListItem<MangaAlternativeTitle>>(),
                i => i.Instance.Value.Title == "C"
            )
        );

        // Remove the "C" alternative title by clicking its row's delete button.
        IRenderedComponent<MudListItem<MangaAlternativeTitle>> cItem = component
            .FindComponents<MudListItem<MangaAlternativeTitle>>()
            .First(i => i.Instance.Value.Title == "C");
        IElement removeButton = cItem.Find("button");
        await component.InvokeAsync(() => removeButton.Click(new MouseEventArgs()));

        MudForm form = component.FindComponent<MudForm>().Instance;
        await SubmitValidFormAsync(component, form);

        component.WaitForAssertion(() =>
        {
            using var db = _testDb.Database.CreateContext();
            Manga persisted = db
                .MangaSeries.Include(m => m.OtherTitles)
                .First(m => m.Id == manga.Id);
            Assert.Equal("D", persisted.PrimaryTitle);
            Assert.Equal(
                new[] { "A", "B" },
                persisted.OtherTitles.Select(t => t.Title).OrderBy(t => t).ToArray()
            );
        });
    }

    // ---------------------------------------------------------------- other save fields

    [Fact]
    public async Task EditManga_SaveProfileAndChapterMerging_PersistsFields()
    {
        Manga manga = await SeedMangaAsync("Manga A");
        var profile = new UpscalerProfile
        {
            Name = "Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var component = RenderWithProviders<EditManga>(parameters =>
            parameters.Add(p => p.MangaId, manga.Id)
        );
        component.WaitForAssertion(() =>
            Assert.True(component.FindComponents<MudSelect<int?>>().Any())
        );

        var profileSelect = component.FindComponent<MudSelect<int?>>();
        await component.InvokeAsync(() =>
            profileSelect.Instance.ValueChanged.InvokeAsync(profile.Id)
        );

        var mergeSelect = component.FindComponent<MudSelect<bool?>>();
        await component.InvokeAsync(() => mergeSelect.Instance.ValueChanged.InvokeAsync(true));

        MudForm form = component.FindComponent<MudForm>().Instance;
        await SubmitValidFormAsync(component, form);

        component.WaitForAssertion(() =>
        {
            using var db = _testDb.Database.CreateContext();
            Manga persisted = db.MangaSeries.First(m => m.Id == manga.Id);
            Assert.Equal(profile.Id, persisted.UpscalerProfilePreferenceId);
            Assert.True(persisted.MergeChapterParts);
        });
    }

    // ---------------------------------------------------------------- invalid form

    [Fact]
    public async Task EditManga_SaveWithInvalidForm_DoesNotWriteAndReportsError()
    {
        Manga manga = await SeedMangaAsync("Manga A");

        var component = RenderWithProviders<EditManga>(parameters =>
            parameters.Add(p => p.MangaId, manga.Id)
        );
        component.WaitForAssertion(() =>
            Assert.Contains(
                component.FindComponents<MudInput<string>>(),
                i => i.Instance.GetState(x => x.Value) == "Manga A"
            )
        );

        // Clear the required primary title, which makes the form invalid.
        IRenderedComponent<MudInput<string>> titleInput = component
            .FindComponents<MudInput<string>>()
            .First(i => i.Instance.GetState(x => x.Value) == "Manga A");
        await component.InvokeAsync(() => titleInput.Instance.ValueChanged.InvokeAsync(""));
        await component.InvokeAsync(() =>
            component.FindComponent<MudForm>().Instance.ValidateAsync()
        );

        // The button is disabled for invalid forms, so invoke the handler directly to exercise the
        // guard that reports the validation error.
        MethodInfo save = typeof(EditManga).GetMethod(
            "Save",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;
        await component.InvokeAsync(() => (Task)save.Invoke(component.Instance, null)!);

        _subSnackbar
            .Received(1)
            .Add(
                Arg.Any<string>(),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>>(),
                Arg.Any<string>()
            );

        using var db = _testDb.Database.CreateContext();
        Assert.Equal("Manga A", db.MangaSeries.First(m => m.Id == manga.Id).PrimaryTitle);
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public async Task EditManga_Cancel_DoesNotWriteChanges()
    {
        Manga manga = await SeedMangaAsync("Manga A");

        var component = RenderWithProviders<EditManga>(parameters =>
            parameters.Add(p => p.MangaId, manga.Id)
        );
        component.WaitForAssertion(() =>
            Assert.Contains(
                component.FindComponents<MudInput<string>>(),
                i => i.Instance.GetState(x => x.Value) == "Manga A"
            )
        );

        IRenderedComponent<MudInput<string>> titleInput = component
            .FindComponents<MudInput<string>>()
            .First(i => i.Instance.GetState(x => x.Value) == "Manga A");
        await component.InvokeAsync(() =>
            titleInput.Instance.ValueChanged.InvokeAsync("Changed But Not Saved")
        );

        IElement cancelLink = component
            .FindAll("a")
            .First(a => a.TextContent.Contains("Cancel", StringComparison.Ordinal));
        Assert.Equal("mangas", cancelLink.GetAttribute("href"));
        await component.InvokeAsync(() => cancelLink.Click(new MouseEventArgs()));

        using var db = _testDb.Database.CreateContext();
        Assert.Equal("Manga A", db.MangaSeries.First(m => m.Id == manga.Id).PrimaryTitle);
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
