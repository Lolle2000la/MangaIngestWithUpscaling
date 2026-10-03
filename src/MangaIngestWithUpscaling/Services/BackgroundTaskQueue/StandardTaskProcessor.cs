using System.Threading.Channels;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class StandardTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<StandardTaskProcessor> logger,
    ITaskPersistenceService taskPersistenceService
) : BackgroundTaskProcessorBase(taskQueue, scopeFactory, logger, taskPersistenceService)
{
    private ChannelReader<object> Reader => TaskQueue.StandardReader;

    protected override string ProcessingFailedLogMessage => "Error processing task {TaskId}";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Reader.ReadAsync(stoppingToken);
            var task = TaskQueue.DequeueStandard();

            if (task == null)
            {
                continue;
            }

            CancellationTokenSource taskStoppingToken = BeginCurrentTask(task, stoppingToken);

            try
            {
                await ProcessTaskAsync(task, taskStoppingToken.Token);
            }
            finally
            {
                DisposeCurrentStoppingToken(taskStoppingToken);
            }
        }
    }

    protected override async Task<bool> TryAcquireTaskAsync(
        PersistedTask task,
        CancellationToken stoppingToken
    )
    {
        if (task.Data is ApplySplitsTask applySplits)
        {
            using IServiceScope scope = ScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (
                await HasSameChapterUpscaleTaskAsync(
                    dbContext,
                    applySplits.ChapterId,
                    stoppingToken
                )
            )
            {
                // ApplySplitsTask rewrites the original CBZ that a same-chapter upscale/repair/detect
                // streams from, and this processor runs concurrently with the upscale processor, so
                // defer it while one of those is pending or in flight to keep the chapter's chain
                // ordered. A deferral is not a failure: the row stays Pending and is re-offered until the
                // blocker finishes (only a restart leaves it to the periodic replayer).
                DeferTask(
                    task,
                    "a same-chapter upscale task for this chapter is pending or in flight"
                );
                return false;
            }
        }

        return await ClaimAsync(task, stoppingToken);
    }

    /// <summary>
    /// True when a same-chapter upscale-family task is pending or in flight. <see cref="ApplySplitsTask"/>
    /// must not run concurrently with one: it rewrites the original CBZ that a worker streams from.
    /// </summary>
    internal static Task<bool> HasSameChapterUpscaleTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        CancellationToken cancellationToken
    ) =>
        AnyChapterTaskAsync(
            context,
            chapterId,
            [
                nameof(UpscaleTask),
                nameof(RepairUpscaleTask),
                nameof(DetectSplitCandidatesTask),
                nameof(RenameUpscaledChaptersSeriesTask),
            ],
            [PersistedTaskStatus.Pending, PersistedTaskStatus.Processing],
            cancellationToken
        );

    /// <summary>
    /// True when a same-chapter <see cref="ApplySplitsTask"/> is in flight. The guard above stops an
    /// apply starting under an upscale; this is its symmetric half, so an upscale/repair/detect cannot
    /// start while an apply is rewriting the same original (and upscaled) CBZ. Only a <c>Processing</c>
    /// apply blocks: a merely <c>Pending</c> one is deferred by the guard above whenever an upscale is
    /// pending, so treating it as a blocker here would deadlock the two.
    /// </summary>
    internal static Task<bool> HasSameChapterApplyTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        CancellationToken cancellationToken
    ) =>
        AnyChapterTaskAsync(
            context,
            chapterId,
            [nameof(ApplySplitsTask)],
            [PersistedTaskStatus.Processing],
            cancellationToken
        );

    private static Task<bool> AnyChapterTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        IReadOnlyCollection<string> taskTypes,
        IReadOnlyCollection<PersistedTaskStatus> statuses,
        CancellationToken cancellationToken
    )
    {
        if (context.Database.IsRelational())
        {
            return PersistedTaskQueries
                .ForTaskTypesAndChapters(context, [chapterId], taskTypes, statuses)
                .AnyAsync(cancellationToken);
        }

        // The InMemory provider (unit tests only) cannot translate the JSON Data column, so filter the
        // status-matched rows in memory instead. Production always uses a relational provider.
        var wantedStatuses = statuses.ToHashSet();
        var wantedTypes = taskTypes.ToHashSet();
        return Task.FromResult(
            context
                .PersistedTasks.Where(t => wantedStatuses.Contains(t.Status))
                .AsEnumerable()
                .Any(t =>
                    wantedTypes.Contains(t.Data.GetType().Name)
                    && t.Data is IChapterTask chapterTask
                    && chapterTask.ChapterId == chapterId
                )
        );
    }
}
