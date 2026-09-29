using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.MangaManagement.Chapters;
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
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
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
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI;

/// <summary>
/// Interaction tests for the non-merge actions on <see cref="ChapterList"/> (delete, upscale,
/// split handling). They drive the rendered toolbar/row controls instead of poking private fields,
/// because the table's item type is a private nested record.
/// </summary>
public class ChapterListActionsTests : ChapterListTestBase
{
    // ------------------------------------------------------------------ delete single

    [Fact]
    public async Task DeleteChapter_RemovesRowFromUiAndDatabase()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await ClickRowButton(component, "Chapter 1.1.cbz", "Delete chapter");

        component.WaitForAssertion(() => Assert.Null(FindDataRow(component, "Chapter 1.1.cbz")));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.Chapters.AnyAsync(c => c.Id == chapters[0].Id));
    }

    // ------------------------------------------------------------------ delete recomputes merge cache (B2)

    [Fact]
    public async Task DeleteChapter_RecomputesMergePossibilitiesForRemainingRows()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();

        // Chapters 1.1/1.2 can be merged. Delete chapter 2 and assert their merge buttons survive:
        // the delete invalidates the cache and must recompute it, otherwise every row loses its
        // merge button until the next reload.
        ConfigureMergePossibilities(CreateMergeInfo((chapters[0], chapters[1])));
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );

        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 1.1.cbz", "Merge this chapter"))
        );

        int coordinatorCallsBefore = GetPossibleMergeActionsCallCount();

        await ClickRowButton(component, "Chapter 2.cbz", "Delete chapter");

        component.WaitForAssertion(() => Assert.Null(FindDataRow(component, "Chapter 2.cbz")));

        Assert.True(
            GetPossibleMergeActionsCallCount() > coordinatorCallsBefore,
            "Deleting a chapter must recompute the merge possibilities"
        );
        Assert.True(
            RowHasButton(component, "Chapter 1.1.cbz", "Merge this chapter"),
            "Remaining mergeable rows must keep their merge button after a delete"
        );
    }

    // ------------------------------------------------------------------ delete bulk (B6)

    [Fact]
    public async Task DeleteSelected_AfterReload_DeletesStaleSelectionById()
    {
        // Regression guard for the reference-equality lookup in DeleteChapter: once the list is
        // reloaded (here by DetectSplits), the selected items reference old ChapterItem/Chapter
        // instances. MudTable prunes a selection on reload, so re-add the captured pre-reload item
        // the way a stale circuit could still hold it; deleting it must match by id, not reference,
        // or First(...) throws.
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();
        _subSplitProcessingCoordinator
            .EnqueueDetectionIfPlausibleAsync(Arg.Any<int>())
            .Returns(false);

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await SelectRow(component, "Chapter 1.1.cbz", true);
        object staleItem = CaptureSingleSelection(component.Instance);

        // Force a reload that replaces every item while the captured instance is now stale.
        await InvokeSplitCallback(component, "Chapter 1.1.cbz", pill => pill.DetectSplitsCallback);

        AddSelection(component.Instance, staleItem);
        component.Render(builder => builder.Add(p => p.Manga, manga));

        component.WaitForAssertion(() =>
            Assert.False(IsDisabled(FindToolbarButton(component, "Delete Selected")))
        );

        await ClickToolbarButton(component, "Delete Selected");

        component.WaitForAssertion(() => Assert.Null(FindDataRow(component, "Chapter 1.1.cbz")));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.Chapters.AnyAsync(c => c.Id == chapters[0].Id));
    }

    // ------------------------------------------------------------------ delete upscaled

    [Fact]
    public async Task DeleteUpscaledChapter_ClearsUpscaledFlagAndShowsUpscaleButton()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync(
            chapter3Upscaled: true
        );
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 2.cbz", "Delete upscaled"))
        );

        await ClickRowButton(component, "Chapter 2.cbz", "Delete upscaled");

        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 2.cbz", "Upscale"))
        );

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Chapter? tracked = await verifyDb.Chapters.FirstAsync(c => c.Id == chapters[2].Id);
        Assert.False(tracked.IsUpscaled);
    }

    [Fact]
    public async Task DeleteUpscaledSelected_ClearsUpscaledFlagForSelection()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync(
            chapter3Upscaled: true
        );
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 2.cbz")));

        await SelectRow(component, "Chapter 2.cbz", true);

        await ClickToolbarButton(component, "Delete Upscaled Selected");

        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 2.cbz", "Upscale"))
        );

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Chapter? tracked = await verifyDb.Chapters.FirstAsync(c => c.Id == chapters[2].Id);
        Assert.False(tracked.IsUpscaled);
    }

    // ------------------------------------------------------------------ toolbar enablement

    [Fact]
    public async Task ToolbarButtons_TrackSelectionState()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        AddMergedChapterInfo(chapters[2].Id);
        ConfigureMergePossibilities(CreateMergeInfo((chapters[0], chapters[1])));
        ConfigureRevertService(mergedChapterId: chapters[2].Id);

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        Assert.True(IsDisabled(FindToolbarButton(component, "Delete Selected")));
        Assert.True(IsDisabled(FindToolbarButton(component, "Merge Selected")));
        Assert.True(IsDisabled(FindToolbarButton(component, "Revert Selected")));

        // A non-merged, mergeable chapter enables delete + merge, but not revert.
        await SelectRow(component, "Chapter 1.1.cbz", true);
        component.WaitForAssertion(() =>
        {
            Assert.False(IsDisabled(FindToolbarButton(component, "Delete Selected")));
            Assert.False(IsDisabled(FindToolbarButton(component, "Merge Selected")));
            Assert.True(IsDisabled(FindToolbarButton(component, "Revert Selected")));
        });

        // Selecting a merged chapter disables merge and enables revert.
        await SelectRow(component, "Chapter 1.1.cbz", false);
        await SelectRow(component, "Chapter 2.cbz", true);
        component.WaitForAssertion(() =>
        {
            Assert.True(IsDisabled(FindToolbarButton(component, "Merge Selected")));
            Assert.False(IsDisabled(FindToolbarButton(component, "Revert Selected")));
        });
    }

    // ------------------------------------------------------------------ upscale selected

    [Fact]
    public async Task UpscaleSelected_EnqueuesOneTaskPerNonUpscaledChapter()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        await AddUpscalerProfileAsync(library);
        ConfigureNoMergePossibilities();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await SelectRow(component, "Chapter 1.1.cbz", true);
        await SelectRow(component, "Chapter 1.2.cbz", true);

        await ClickToolbarButton(component, "Upscale Selected");

        component.WaitForAssertion(() =>
            _subTaskQueue.Received(2).EnqueueAsync(Arg.Any<UpscaleTask>())
        );
    }

    [Fact]
    public async Task UpscaleSelected_WithoutProfile_DoesNotEnqueueAndShowsError()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await SelectRow(component, "Chapter 1.1.cbz", true);

        await ClickToolbarButton(component, "Upscale Selected");

        component.WaitForAssertion(() =>
            _subSnackbar
                .Received(1)
                .Add(
                    Arg.Is<string>(m => m.Contains("Snackbar_UpscalerProfileNotSet")),
                    Severity.Error,
                    Arg.Any<Action<SnackbarOptions>?>(),
                    Arg.Any<string?>()
                )
        );
        await _subTaskQueue.DidNotReceive().EnqueueAsync(Arg.Any<UpscaleTask>());
    }

    // ------------------------------------------------------------------ apply splits

    [Fact]
    public async Task ApplySplits_AppliesAndResetsState()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await InvokeSplitCallback(component, "Chapter 1.1.cbz", pill => pill.ApplySplitsCallback);

        await _subSplitApplicationService
            .Received(1)
            .ApplySplitsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _subSplitStateManager
            .Received(1)
            .ResetToPendingAsync(
                Arg.Any<int>(),
                Arg.Any<ApplicationDbContext?>(),
                Arg.Any<CancellationToken>()
            );
    }

    // ------------------------------------------------------------------ manual split result

    [Fact]
    public async Task ManualSplit_OkResult_ReloadsChapterList()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        MockManualSplitDialog(DialogResult.Ok(true));

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        int metadataCallsBefore = GetMetadataCallCount();

        await InvokeSplitCallback(component, "Chapter 1.1.cbz", pill => pill.ManualSplitCallback);

        component.WaitForAssertion(() =>
            Assert.True(
                GetMetadataCallCount() > metadataCallsBefore,
                "An accepted manual split must reload the chapter list"
            )
        );
    }

    [Fact]
    public async Task ManualSplit_CancelResult_DoesNotReloadChapterList()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        MockManualSplitDialog(DialogResult.Cancel());

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        int metadataCallsBefore = GetMetadataCallCount();

        await InvokeSplitCallback(component, "Chapter 1.1.cbz", pill => pill.ManualSplitCallback);

        // Give the (non-)reload a chance to run before asserting nothing happened.
        await Task.Delay(50);
        Assert.Equal(metadataCallsBefore, GetMetadataCallCount());
    }

    // ------------------------------------------------------------------ delete clears stale selection (B2)

    [Fact]
    public async Task DeleteSelected_StaleSelection_IsPrunedSoToolbarDisables()
    {
        // Deleting a chapter whose selected instance is still current is pruned by MudBlazor itself
        // (TableContext.Remove handles the disposed row). This variant covers a selection captured
        // before a reload: the stale instance no longer matches any row, so there is nothing for
        // MudBlazor to prune and the toolbar would stay enabled unless the component prunes by id.
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();
        _subSplitProcessingCoordinator
            .EnqueueDetectionIfPlausibleAsync(Arg.Any<int>())
            .Returns(false);

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await SelectRow(component, "Chapter 1.1.cbz", true);
        object staleItem = CaptureSingleSelection(component.Instance);

        // Force a reload that replaces every item while the captured instance becomes stale.
        await InvokeSplitCallback(component, "Chapter 1.1.cbz", pill => pill.DetectSplitsCallback);

        AddSelection(component.Instance, staleItem);
        component.Render(builder => builder.Add(p => p.Manga, manga));

        component.WaitForAssertion(() =>
            Assert.False(IsDisabled(FindToolbarButton(component, "Delete Selected")))
        );

        await ClickToolbarButton(component, "Delete Selected");

        component.WaitForAssertion(() => Assert.Null(FindDataRow(component, "Chapter 1.1.cbz")));

        Assert.True(
            IsDisabled(FindToolbarButton(component, "Delete Selected")),
            "Deleting a chapter selected under a stale instance must still clear the selection"
        );
        Assert.True(IsDisabled(FindToolbarButton(component, "Upscale Selected")));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.Chapters.AnyAsync(c => c.Id == chapters[0].Id));
    }

    // ------------------------------------------------------------------ missing backing files (B6)

    [Fact]
    public async Task DeleteChapter_MissingBackingFile_StillReconcilesRowAndDatabase()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();

        // Simulate the file disappearing behind the app's back.
        File.Delete(Path.Combine(_libraryPath, "Chapter 1.1.cbz"));

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz")));

        await ClickRowButton(component, "Chapter 1.1.cbz", "Delete chapter");

        component.WaitForAssertion(() => Assert.Null(FindDataRow(component, "Chapter 1.1.cbz")));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.Chapters.AnyAsync(c => c.Id == chapters[0].Id));
    }

    [Fact]
    public async Task DeleteUpscaledChapter_MissingUpscaledFile_ClearsFlag()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync(
            chapter3Upscaled: true
        );
        ConfigureNoMergePossibilities();
        ConfirmAllMessageBoxes();

        // Simulate the upscaled file disappearing behind the app's back.
        File.Delete(Path.Combine(_upscaledPath, "Chapter 2.cbz"));

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 2.cbz", "Delete upscaled"))
        );

        await ClickRowButton(component, "Chapter 2.cbz", "Delete upscaled");

        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 2.cbz", "Upscale"))
        );

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Chapter? tracked = await verifyDb.Chapters.FirstAsync(c => c.Id == chapters[2].Id);
        Assert.False(tracked.IsUpscaled);
    }
}

/// <summary>
/// The B1 regression needs the real <see cref="ChapterMergeCoordinator"/>: a substitute would never
/// reproduce the duplicate-key graph-attach throw from mixing two DbContext graphs.
/// </summary>
public class ChapterListRevertRegressionTests : ChapterListTestBase
{
    protected override void RegisterCoordinator()
    {
        _subChapterPartMerger
            .GroupChapterPartsForMerging(
                Arg.Any<IEnumerable<FoundChapter>>(),
                Arg.Any<Func<string, bool>>()
            )
            .Returns(callInfo =>
            {
                var found = callInfo.Arg<IEnumerable<FoundChapter>>().ToList();
                var parts = found
                    .Where(f => f.FileName is "Chapter 1.1.cbz" or "Chapter 1.2.cbz")
                    .ToList();
                var groups = new Dictionary<string, List<FoundChapter>>();
                if (parts.Count > 0)
                {
                    groups["1"] = parts;
                }

                return groups;
            });
        _subChapterPartMerger
            .GroupChaptersForAdditionToExistingMerged(
                Arg.Any<IEnumerable<FoundChapter>>(),
                Arg.Any<HashSet<string>>(),
                Arg.Any<Dictionary<string, List<string>>>(),
                Arg.Any<Func<string, bool>>()
            )
            .Returns(new Dictionary<string, List<FoundChapter>>());

        Services.AddSingleton(Substitute.For<ILogger<ChapterMergeCoordinator>>());
        Services.AddSingleton<IChapterPartMerger>(_subChapterPartMerger);
        Services.AddSingleton<IChapterMergeUpscaleTaskManager>(_subChapterMergeUpscaleTaskManager);
        Services.AddSingleton<IChapterMergeCoordinator>(sp => new ChapterMergeCoordinator(
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            sp.GetRequiredService<IChapterPartMerger>(),
            sp.GetRequiredService<IChapterMergeUpscaleTaskManager>(),
            sp.GetRequiredService<ITaskQueue>(),
            sp.GetRequiredService<IMetadataHandlingService>(),
            sp.GetRequiredService<ISplitProcessingCoordinator>(),
            sp.GetRequiredService<IStringLocalizer<ChapterMergeCoordinator>>(),
            sp.GetRequiredService<ILogger<ChapterMergeCoordinator>>()
        ));
    }

    [Fact]
    public async Task RevertMergedChapter_RealCoordinator_RebuildsFromSingleContext()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateRevertableMangaAsync();

        ConfigureRevertService(mergedChapterId: chapters[0].Id);
        ConfigureRevertMockToRestoreTwoParts();
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(
            () =>
                Assert.True(RowHasButton(component, "Chapter 1.cbz", "Revert this merged chapter")),
            TimeSpan.FromSeconds(20)
        );

        await ClickRowButton(component, "Chapter 1.cbz", "Revert this merged chapter");

        // The restored parts must render...
        component.WaitForAssertion(
            () =>
            {
                Assert.NotNull(FindDataRow(component, "Chapter 1.1.cbz"));
                Assert.NotNull(FindDataRow(component, "Chapter 1.2.cbz"));
            },
            TimeSpan.FromSeconds(20)
        );

        // ...the success snackbar must be shown (no spurious failure)...
        component.WaitForAssertion(
            () =>
                _subSnackbar
                    .Received(1)
                    .Add(
                        Arg.Is<string>(m => m.Contains("Snackbar_RevertMergedChapter_Success")),
                        Severity.Success,
                        Arg.Any<Action<SnackbarOptions>?>(),
                        Arg.Any<string?>()
                    ),
            TimeSpan.FromSeconds(20)
        );
        _subSnackbar
            .DidNotReceive()
            .Add(
                Arg.Is<string>(m => m.Contains("Snackbar_RevertMergedChapter_Failure")),
                Arg.Any<Severity>(),
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string?>()
            );

        // ...and the real coordinator must have recomputed the cache for the restored parts, which
        // is what lights up their merge buttons. With the mixed graph it threw and no button
        // appeared.
        component.WaitForAssertion(
            () =>
                Assert.True(
                    RowHasButton(component, "Chapter 1.1.cbz", "Merge this chapter"),
                    "Restored parts should be mergeable once the list is rebuilt from one context"
                ),
            TimeSpan.FromSeconds(20)
        );
    }

    private void ConfigureRevertMockToRestoreTwoParts()
    {
        _subRevertService
            .RevertMergedChapterAsync(
                Arg.Any<Chapter>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ApplicationDbContext>()
            )
            .Returns(async callInfo =>
            {
                int mergedId = callInfo.Arg<Chapter>().Id;
                await using (var writeDb = await _testDb.Database.CreateContextAsync())
                {
                    Chapter merged = await writeDb.Chapters.FirstAsync(c => c.Id == mergedId);
                    writeDb.Chapters.Remove(merged);
                    writeDb.Chapters.AddRange(
                        new Chapter
                        {
                            FileName = "Chapter 1.1.cbz",
                            RelativePath = "Chapter 1.1.cbz",
                            MangaId = merged.MangaId,
                            Manga = null!,
                            IsUpscaled = false,
                        },
                        new Chapter
                        {
                            FileName = "Chapter 1.2.cbz",
                            RelativePath = "Chapter 1.2.cbz",
                            MangaId = merged.MangaId,
                            Manga = null!,
                            IsUpscaled = false,
                        }
                    );
                    await writeDb.SaveChangesAsync(CancellationToken.None);
                }

                // Return the restored entities from their own context, exactly as the real service
                // does, so they carry a second Manga graph.
                await using var readDb = await _testDb.Database.CreateContextAsync();
                Manga reloaded = await readDb
                    .MangaSeries.Include(m => m.Library)
                    .Include(m => m.Chapters)
                    .FirstAsync(m => m.Id == 1);
                return reloaded
                    .Chapters.Where(c => c.FileName is "Chapter 1.1.cbz" or "Chapter 1.2.cbz")
                    .ToList();
            });
    }
}

/// <summary>
/// Shared harness for chapter-list interaction tests: an isolated database, temp library paths with
/// real chapter files, and the component's DI graph.
/// </summary>
public abstract class ChapterListTestBase : BunitContext
{
    protected TestDatabaseHelper.TestDbContext _testDb = null!;
    protected ApplicationDbContext _dbContext = null!;
    protected string _libraryPath = null!;
    protected string _upscaledPath = null!;

    protected IChapterChangedNotifier _subChapterChangedNotifier = null!;
    protected IDialogService _subDialogService = null!;
    protected IFileSystem _subFileSystem = null!;
    protected ILibraryIntegrityChecker _subLibraryIntegrityChecker = null!;
    protected IMangaMetadataChanger _subMangaMetadataChanger = null!;
    protected IChapterMergeCoordinator _subMergeCoordinator = null!;
    protected IMetadataHandlingService _subMetadataHandler = null!;
    protected IChapterMergeRevertService _subRevertService = null!;
    protected ISnackbar _subSnackbar = null!;
    protected ISplitApplicationService _subSplitApplicationService = null!;
    protected ISplitProcessingService _subSplitProcessingService = null!;
    protected ISplitProcessingCoordinator _subSplitProcessingCoordinator = null!;
    protected ISplitProcessingStateManager _subSplitStateManager = null!;
    protected IManualSplitService _subManualSplitService = null!;
    protected ITaskQueue _subTaskQueue = null!;
    protected IWebHostEnvironment _subWebHostEnvironment = null!;
    protected IChapterPartMerger _subChapterPartMerger = null!;
    protected IChapterMergeUpscaleTaskManager _subChapterMergeUpscaleTaskManager = null!;

    protected ChapterListTestBase()
    {
        _libraryPath = Path.Combine(
            Path.GetTempPath(),
            "manga-chapterlist-tests",
            Guid.NewGuid().ToString("N")
        );
        _upscaledPath = _libraryPath + "-upscaled";
        Directory.CreateDirectory(_libraryPath);
        Directory.CreateDirectory(_upscaledPath);

        SetupMocks();
        SetupDatabase();
        RegisterServices();
    }

    private void SetupMocks()
    {
        _subMergeCoordinator = Substitute.For<IChapterMergeCoordinator>();
        _subRevertService = Substitute.For<IChapterMergeRevertService>();
        _subMetadataHandler = Substitute.For<IMetadataHandlingService>();
        _subFileSystem = Substitute.For<IFileSystem>();
        _subTaskQueue = Substitute.For<ITaskQueue>();
        _subChapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        _subLibraryIntegrityChecker = Substitute.For<ILibraryIntegrityChecker>();
        _subMangaMetadataChanger = Substitute.For<IMangaMetadataChanger>();
        _subWebHostEnvironment = Substitute.For<IWebHostEnvironment>();
        _subSnackbar = Substitute.For<ISnackbar>();
        _subSplitApplicationService = Substitute.For<ISplitApplicationService>();
        _subSplitProcessingService = Substitute.For<ISplitProcessingService>();
        _subSplitProcessingCoordinator = Substitute.For<ISplitProcessingCoordinator>();
        _subSplitStateManager = Substitute.For<ISplitProcessingStateManager>();
        _subManualSplitService = new MockManualSplitService();
        _subDialogService = Substitute.For<IDialogService>();
        _subChapterPartMerger = Substitute.For<IChapterPartMerger>();
        _subChapterMergeUpscaleTaskManager = Substitute.For<IChapterMergeUpscaleTaskManager>();

        _subWebHostEnvironment.EnvironmentName.Returns("Test");
        _subMetadataHandler
            .GetSeriesAndTitleFromComicInfoAsync(Arg.Any<string>())
            .Returns(Task.FromResult(new ExtractedMetadata("Test Series", "Test Chapter", "1")));
        _subMergeCoordinator
            .GetPossibleMergeActionsAsync(Arg.Any<List<Chapter>>(), Arg.Any<bool>())
            .Returns(new MergeActionInfo());
    }

    private void SetupDatabase()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;
    }

    private void RegisterServices()
    {
        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subRevertService);
        Services.AddSingleton(_subMetadataHandler);
        Services.AddSingleton(_subWebHostEnvironment);
        Services.AddSingleton(_subTaskQueue);
        Services.AddSingleton(_subChapterChangedNotifier);
        Services.AddSingleton(_subLibraryIntegrityChecker);
        Services.AddSingleton(_subMangaMetadataChanger);
        Services.AddSingleton(_subSnackbar);
        Services.AddSingleton(_subSplitApplicationService);
        Services.AddSingleton(_subSplitProcessingService);
        Services.AddSingleton(_subSplitProcessingCoordinator);
        Services.AddSingleton(_subSplitStateManager);
        Services.AddSingleton(_subManualSplitService);
        Services.AddSingleton(_subDialogService);
        Services.AddSingleton(_subFileSystem);
        Services.AddSingleton(Substitute.For<ILogger<ChapterList>>());
        Services.AddSingleton(Substitute.For<ILogger<ManualSplitDialog>>());

        RegisterCoordinator();

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

    protected virtual void RegisterCoordinator()
    {
        Services.AddSingleton(_subMergeCoordinator);
    }

    // ------------------------------------------------------------------ data

    protected async Task<(
        Manga manga,
        Library library,
        List<Chapter> chapters
    )> CreateTestDataAsync(bool chapter3Upscaled = false)
    {
        var library = new Library
        {
            Id = 1,
            Name = "Test Library",
            NotUpscaledLibraryPath = _libraryPath,
            UpscaledLibraryPath = _upscaledPath,
        };

        var manga = new Manga
        {
            Id = 1,
            PrimaryTitle = "Test Manga",
            Library = library,
            LibraryId = library.Id,
        };

        var chapters = new List<Chapter>
        {
            CreateChapter(1, manga, "Chapter 1.1.cbz"),
            CreateChapter(2, manga, "Chapter 1.2.cbz"),
            CreateChapter(3, manga, "Chapter 2.cbz", chapter3Upscaled),
        };
        manga.Chapters = chapters;

        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.Add(manga);
        _dbContext.Chapters.AddRange(chapters);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        return (manga, library, chapters);
    }

    protected async Task<(
        Manga manga,
        Library library,
        List<Chapter> chapters
    )> CreateRevertableMangaAsync()
    {
        // Do not assign explicit ids: on PostgreSQL an explicit id does not advance the identity
        // sequence, so a later insert reuses the id of the merged chapter deleted during the revert
        // and the restored parts are then mistaken for that old merged chapter.
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = _libraryPath,
            UpscaledLibraryPath = _upscaledPath,
        };

        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };

        Chapter Create(string fileName)
        {
            WriteFile(Path.Combine(_libraryPath, fileName));
            return new Chapter
            {
                FileName = fileName,
                RelativePath = fileName,
                Manga = manga,
                IsUpscaled = false,
            };
        }

        var chapters = new List<Chapter> { Create("Chapter 1.cbz"), Create("Chapter 2.cbz") };
        manga.Chapters = chapters;

        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.Add(manga);
        _dbContext.Chapters.AddRange(chapters);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        _dbContext.MergedChapterInfos.Add(
            new MergedChapterInfo
            {
                ChapterId = chapters[0].Id,
                MergedChapterNumber = "1",
                OriginalParts = new List<OriginalChapterPart>
                {
                    new()
                    {
                        FileName = "Chapter 1.1.cbz",
                        PageNames = new List<string> { "p1.jpg" },
                    },
                    new()
                    {
                        FileName = "Chapter 1.2.cbz",
                        PageNames = new List<string> { "p2.jpg" },
                    },
                },
                CreatedAt = DateTime.UtcNow,
            }
        );
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        return (manga, library, chapters);
    }

    protected Chapter CreateChapter(int id, Manga manga, string fileName, bool upscaled = false)
    {
        WriteFile(Path.Combine(_libraryPath, fileName));
        if (upscaled)
        {
            WriteFile(Path.Combine(_upscaledPath, fileName));
        }

        return new Chapter
        {
            Id = id,
            FileName = fileName,
            RelativePath = fileName,
            Manga = manga,
            MangaId = manga.Id,
            IsUpscaled = upscaled,
        };
    }

    protected void AddMergedChapterInfo(int chapterId)
    {
        _dbContext.MergedChapterInfos.Add(
            new MergedChapterInfo
            {
                ChapterId = chapterId,
                MergedChapterNumber = "2",
                OriginalParts = new List<OriginalChapterPart>
                {
                    new()
                    {
                        FileName = "Chapter 2.1.cbz",
                        PageNames = new List<string> { "p1.jpg" },
                    },
                },
                CreatedAt = DateTime.UtcNow,
            }
        );
        _dbContext.SaveChanges();
    }

    protected async Task AddUpscalerProfileAsync(Library library)
    {
        var profile = new UpscalerProfile
        {
            Id = 1,
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        library.UpscalerProfileId = profile.Id;
        library.UpscalerProfile = profile;
        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "dummy");
    }

    // ------------------------------------------------------------------ mock configuration

    protected void ConfigureNoMergePossibilities()
    {
        _subMergeCoordinator
            .GetPossibleMergeActionsAsync(Arg.Any<List<Chapter>>(), Arg.Any<bool>())
            .Returns(new MergeActionInfo());
    }

    protected void ConfigureMergePossibilities(MergeActionInfo info)
    {
        _subMergeCoordinator
            .GetPossibleMergeActionsAsync(Arg.Any<List<Chapter>>(), Arg.Any<bool>())
            .Returns(info);
    }

    protected static MergeActionInfo CreateMergeInfo(
        params (Chapter first, Chapter second)[] groups
    )
    {
        var info = new MergeActionInfo();
        int index = 0;
        foreach ((Chapter first, Chapter second) in groups)
        {
            info.NewMergeGroups[(++index).ToString()] = new List<Chapter> { first, second };
        }

        return info;
    }

    protected void ConfigureRevertService(int mergedChapterId)
    {
        _subRevertService
            .CanRevertChapterAsync(
                Arg.Any<Chapter>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ApplicationDbContext>()
            )
            .Returns(callInfo => callInfo.Arg<Chapter>().Id == mergedChapterId);
    }

    protected void ConfirmAllMessageBoxes()
    {
        _subDialogService
            .ShowMessageBoxAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DialogOptions>()
            )
            .Returns(Task.FromResult<bool?>(true));
    }

    protected void MockManualSplitDialog(DialogResult result)
    {
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult<DialogResult?>(result));
        _subDialogService
            .ShowAsync<ManualSplitDialog>(
                Arg.Any<string>(),
                Arg.Any<DialogParameters>(),
                Arg.Any<DialogOptions>()
            )
            .Returns(Task.FromResult(reference));
    }

    // ------------------------------------------------------------------ queries

    protected int GetPossibleMergeActionsCallCount()
    {
        return _subMergeCoordinator
            .ReceivedCalls()
            .Count(c =>
                c.GetMethodInfo().Name
                == nameof(IChapterMergeCoordinator.GetPossibleMergeActionsAsync)
            );
    }

    protected int GetMetadataCallCount()
    {
        return _subMetadataHandler
            .ReceivedCalls()
            .Count(c =>
                c.GetMethodInfo().Name
                == nameof(IMetadataHandlingService.GetSeriesAndTitleFromComicInfoAsync)
            );
    }

    // The table item type is a private nested record, so drive its selection set by reflection when
    // a test needs to hold an instance across a reload (the reference-equality regression).
    private static object GetSelectedChaptersSet(ChapterList component)
    {
        return typeof(ChapterList)
            .GetField("selectedChapters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;
    }

    protected static object CaptureSingleSelection(ChapterList component)
    {
        return ((System.Collections.IEnumerable)GetSelectedChaptersSet(component))
            .Cast<object>()
            .Single();
    }

    protected static void AddSelection(ChapterList component, object item)
    {
        object set = GetSelectedChaptersSet(component);
        set.GetType().GetMethod("Add")!.Invoke(set, new[] { item });
    }

    // ------------------------------------------------------------------ interactions

    protected static IElement? FindDataRow(IRenderedComponent<ChapterList> component, string text)
    {
        return component
            .FindAll("tr")
            .FirstOrDefault(tr => tr.QuerySelectorAll("td").Any() && tr.TextContent.Contains(text));
    }

    protected static IElement FindToolbarButton(
        IRenderedComponent<ChapterList> component,
        string text
    )
    {
        return component.FindAll("button").First(b => b.TextContent.Contains(text));
    }

    protected static bool RowHasButton(
        IRenderedComponent<ChapterList> component,
        string rowText,
        string title
    )
    {
        IElement? row = FindDataRow(component, rowText);
        return row is not null
            && row.QuerySelectorAll("button").Any(b => b.GetAttribute("title") == title);
    }

    protected static bool IsDisabled(IElement button)
    {
        return button.HasAttribute("disabled") || button.ClassList.Contains("mud-disabled");
    }

    protected static async Task ClickRowButton(
        IRenderedComponent<ChapterList> component,
        string rowText,
        string title
    )
    {
        await component.InvokeAsync(() =>
        {
            IElement row = FindDataRow(component, rowText)!;
            row.QuerySelectorAll("button").First(b => b.GetAttribute("title") == title).Click();
        });
    }

    protected static async Task ClickToolbarButton(
        IRenderedComponent<ChapterList> component,
        string text
    )
    {
        await component.InvokeAsync(() => FindToolbarButton(component, text).Click());
    }

    protected static async Task SelectRow(
        IRenderedComponent<ChapterList> component,
        string rowText,
        bool selected
    )
    {
        await component.InvokeAsync(() =>
        {
            IElement row = FindDataRow(component, rowText)!;
            IElement checkbox = row.QuerySelector("input.mud-checkbox-input")!;
            checkbox.Change(new ChangeEventArgs { Value = selected });
        });
    }

    protected static async Task InvokeSplitCallback(
        IRenderedComponent<ChapterList> component,
        string rowText,
        Func<SplitStatusPill, EventCallback> selector
    )
    {
        await component.InvokeAsync(async () =>
        {
            List<IElement> dataRows = component
                .FindAll("tr")
                .Where(tr => tr.QuerySelectorAll("td").Any())
                .ToList();
            int rowIndex = dataRows.FindIndex(tr => tr.TextContent.Contains(rowText));
            if (rowIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Data row containing '{rowText}' was not found."
                );
            }

            IRenderedComponent<SplitStatusPill> pill = component
                .FindComponents<SplitStatusPill>()
                .ElementAt(rowIndex);
            await selector(pill.Instance).InvokeAsync();
        });
    }

    // ------------------------------------------------------------------ rendering

    protected IRenderedComponent<T> RenderComponentWithProviders<T>(
        Action<ComponentParameterCollectionBuilder<T>>? parameterBuilder = null
    )
        where T : class, IComponent
    {
        RenderFragment providerFragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
        };
        Render(providerFragment);

        return parameterBuilder != null ? Render<T>(parameterBuilder) : Render<T>();
    }

    // ------------------------------------------------------------------ teardown

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

        TryDeleteDirectory(_libraryPath);
        TryDeleteDirectory(_upscaledPath);

        if (_testDb is not null)
        {
            TestDatabaseHelper.TestDbContext testDb = _testDb;
            _testDb = null!;
            await testDb.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Regression tests for adding chapters to an existing merged chapter: the coordinator result is
/// the source of truth, so a no-op must not remove rows or report success, a real addition must
/// refresh the list and invalidate the merge cache, and a failed merge must not be summarised as a
/// success.
/// </summary>
public class ChapterListMergeAdditionTests : ChapterListTestBase
{
    private void ConfigureAdditionGroup(params Chapter[] chapters)
    {
        ConfigureMergePossibilities(
            new MergeActionInfo
            {
                AdditionsToExistingMerged = new Dictionary<string, List<Chapter>>
                {
                    { "1", chapters.ToList() },
                },
            }
        );
    }

    private void ConfigureAdditionResult(int mergedCount)
    {
        _subMergeCoordinator
            .ProcessExistingChapterPartsForMergingAsync(
                Arg.Any<Manga>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ApplicationDbContext?>()
            )
            .Returns(mergedCount);
    }

    private int CountPossibleMergeActionsCalls(bool includeLatest)
    {
        return _subMergeCoordinator
            .ReceivedCalls()
            .Count(c =>
                c.GetMethodInfo().Name
                    == nameof(IChapterMergeCoordinator.GetPossibleMergeActionsAsync)
                && (bool)c.GetArguments()[1]! == includeLatest
            );
    }

    [Fact]
    public async Task AddToExistingMerged_NoOp_KeepsRowsAndDoesNotReportSuccess()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        AddMergedChapterInfo(chapters[0].Id);
        ConfigureAdditionGroup(chapters[1]);

        // The coordinator reports a no-op (0 chapters merged/added), exactly as it does when
        // merging is disabled or a conflict stops the addition. The reading path never used to
        // check this, removed the row and claimed success anyway.
        ConfigureAdditionResult(0);
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 1.2.cbz", "Merge this chapter"))
        );

        await ClickRowButton(component, "Chapter 1.2.cbz", "Merge this chapter");

        await _subMergeCoordinator
            .Received()
            .ProcessExistingChapterPartsForMergingAsync(
                Arg.Any<Manga>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ApplicationDbContext?>()
            );

        // Nothing changed, so the row must remain and no success may be shown.
        component.WaitForAssertion(() => Assert.NotNull(FindDataRow(component, "Chapter 1.2.cbz")));
        _subSnackbar
            .DidNotReceive()
            .Add(
                Arg.Is<string>(m => m.Contains("Snackbar_AddChaptersSuccess")),
                Arg.Any<Severity>(),
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string?>()
            );

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.True(await verifyDb.Chapters.AnyAsync(c => c.Id == chapters[1].Id));
    }

    [Fact]
    public async Task AddToExistingMerged_RecomputesMergePossibilities()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        AddMergedChapterInfo(chapters[0].Id);
        ConfigureAdditionGroup(chapters[1]);
        ConfigureAdditionResult(1);
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 1.2.cbz", "Merge this chapter"))
        );

        int latestCallsBefore = CountPossibleMergeActionsCalls(includeLatest: true);

        await ClickRowButton(component, "Chapter 1.2.cbz", "Merge this chapter");

        // One "include latest" call is the confirm path; a second proves the cache was invalidated
        // and recomputed. Without the invalidate the 5s guard skipped the recompute.
        component.WaitForAssertion(() =>
            Assert.True(
                CountPossibleMergeActionsCalls(includeLatest: true) - latestCallsBefore >= 2,
                "A real addition must invalidate and recompute the merge possibilities"
            )
        );

        _subSnackbar
            .Received()
            .Add(
                Arg.Is<string>(m => m.Contains("Snackbar_AddChaptersSuccess")),
                Severity.Success,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string?>()
            );
    }

    [Fact]
    public async Task MergeOperations_NothingMerged_DoesNotShowSuccessSummary()
    {
        (Manga manga, Library library, List<Chapter> chapters) = await CreateTestDataAsync();
        ConfigureMergePossibilities(CreateMergeInfo((chapters[0], chapters[1])));

        // The coordinator reports that it merged nothing.
        _subMergeCoordinator
            .MergeSelectedChaptersAsync(Arg.Any<List<Chapter>>(), Arg.Any<bool>())
            .Returns(new List<MergeInfo>());
        ConfirmAllMessageBoxes();

        var component = RenderComponentWithProviders<ChapterList>(parameters =>
            parameters.Add(p => p.Manga, manga)
        );
        component.WaitForAssertion(() =>
            Assert.True(RowHasButton(component, "Chapter 1.1.cbz", "Merge this chapter"))
        );

        await SelectRow(component, "Chapter 1.1.cbz", true);
        component.WaitForAssertion(() =>
            Assert.False(IsDisabled(FindToolbarButton(component, "Merge Selected")))
        );

        await ClickToolbarButton(component, "Merge Selected");

        component.WaitForAssertion(() =>
            _subSnackbar
                .Received()
                .Add(
                    Arg.Is<string>(m => m.Contains("Snackbar_NoChaptersMerged")),
                    Severity.Warning,
                    Arg.Any<Action<SnackbarOptions>?>(),
                    Arg.Any<string?>()
                )
        );

        // The warning must not be followed by a success summary that counts the attempted merge.
        _subSnackbar
            .DidNotReceive()
            .Add(
                Arg.Is<string>(m => m.Contains("Snackbar_MergeOperationsSuccess")),
                Arg.Any<Severity>(),
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string?>()
            );
    }
}
