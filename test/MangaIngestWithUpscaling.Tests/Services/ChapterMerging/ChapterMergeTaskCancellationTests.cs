using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.ChapterMerging;

/// <summary>
/// The merge manager cancels the chapter-scoped tasks it finds. <see cref="ApplySplitsTask"/> is no
/// longer an upscale-family task, so it runs on the standard processor: cancelling it through the
/// upscale processor matched nothing and silently did nothing, which let a live apply keep rewriting
/// the original CBZ a merge was manipulating.
/// </summary>
public class ChapterMergeTaskCancellationTests : IAsyncDisposable
{
    private readonly TestDatabase _database;

    public ChapterMergeTaskCancellationTests()
    {
        _database = TestDatabaseFactory.Create();
        using (ApplicationDbContext schema = _database.CreateContext()) { }
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelsEachRunningChapterTaskOnTheProcessorThatOwnsIt()
    {
        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        var library = new Library { Name = "Merge Library" };
        context.Libraries.Add(library);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga { PrimaryTitle = "Merge Manga", LibraryId = library.Id };
        context.MangaSeries.Add(manga);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
        };
        context.Chapters.Add(chapter);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var apply = new PersistedTask
        {
            Data = new ApplySplitsTask(chapter.Id, 1),
            Status = PersistedTaskStatus.Processing,
            Order = 1,
        };
        var upscale = new PersistedTask
        {
            Data = new UpscaleTask { ChapterId = chapter.Id, UpscalerProfileId = 1 },
            Status = PersistedTaskStatus.Processing,
            Order = 2,
        };
        context.PersistedTasks.AddRange(apply, upscale);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        using ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var queue = new TaskQueue(scopeFactory, NullLogger<TaskQueue>.Instance);
        var persistence = Substitute.For<ITaskPersistenceService>();

        // Substitutes only to observe which processor was asked: CancelCurrent is a no-op unless that
        // processor is running the task, which is the whole failure mode.
        var upscaleProcessor = Substitute.For<UpscaleTaskProcessor>(
            queue,
            scopeFactory,
            Options.Create(new UpscalerConfig()),
            NullLogger<UpscaleTaskProcessor>.Instance,
            persistence,
            new PreprocessedInputCache()
        );
        var standardProcessor = Substitute.For<StandardTaskProcessor>(
            queue,
            scopeFactory,
            NullLogger<StandardTaskProcessor>.Instance,
            persistence
        );
        var distributedProcessor = Substitute.For<DistributedUpscaleTaskProcessor>(
            queue,
            scopeFactory,
            Options.Create(new UpscalerConfig()),
            NullLogger<DistributedUpscaleTaskProcessor>.Instance,
            persistence
        );

        var manager = new ChapterMergeUpscaleTaskManager(
            context,
            Substitute.For<ITaskQueue>(),
            upscaleProcessor,
            distributedProcessor,
            standardProcessor,
            Substitute.For<ISplitProcessingCoordinator>(),
            NullLogger<ChapterMergeUpscaleTaskManager>.Instance
        );

        await manager.HandleUpscaleTaskManagementAsync(
            [chapter],
            new MergeInfo(
                new FoundChapter(
                    "chapter1.cbz",
                    "chapter1.cbz",
                    ChapterStorageType.Cbz,
                    new ExtractedMetadata("Merge Manga", "Chapter 1", "1")
                ),
                new List<OriginalChapterPart>(),
                "1"
            ),
            library,
            null,
            TestContext.Current.CancellationToken
        );

        // At least once: the manager re-cancels after its cancellation wait when the task is still
        // Processing, so the exact count is not the point — which processor was asked is.
        upscaleProcessor.Received().CancelCurrent(Arg.Is<PersistedTask>(t => t.Id == upscale.Id));
        standardProcessor.Received().CancelCurrent(Arg.Is<PersistedTask>(t => t.Id == apply.Id));
        // RemoteOnly owns genuine upscales in the distributed processor, so the upscale must be
        // cancelled there too or the merge proceeds while a worker is still writing the chapter.
        await distributedProcessor
            .Received()
            .CancelCurrent(Arg.Is<PersistedTask>(t => t.Id == upscale.Id));

        // Regression guard: the apply used to be cancelled through the upscale processor, and the
        // upscale-family task is never cancelled through the standard one.
        upscaleProcessor
            .DidNotReceive()
            .CancelCurrent(Arg.Is<PersistedTask>(t => t.Id == apply.Id));
        standardProcessor
            .DidNotReceive()
            .CancelCurrent(Arg.Is<PersistedTask>(t => t.Id == upscale.Id));
        await distributedProcessor
            .DidNotReceive()
            .CancelCurrent(Arg.Is<PersistedTask>(t => t.Id == apply.Id));
    }
}
