using System.Text.Json;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeUpscaleTaskManager(
    ApplicationDbContext dbContext,
    ITaskQueue taskQueue,
    UpscaleTaskProcessor upscaleTaskProcessor,
    ISplitProcessingCoordinator splitProcessingCoordinator,
    ILogger<ChapterMergeUpscaleTaskManager> logger
) : IChapterMergeUpscaleTaskManager
{
    private static readonly string[] ChapterScopedTaskTypes =
    {
        nameof(UpscaleTask),
        nameof(RepairUpscaleTask),
        nameof(RenameUpscaledChaptersSeriesTask),
        nameof(DetectSplitCandidatesTask),
        nameof(ApplySplitsTask),
    };

    public async Task HandleUpscaleTaskManagementAsync(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        UpscaledMergeResult? upscaledMergeResult = null,
        CancellationToken cancellationToken = default
    )
    {
        List<int> chapterIds = originalChapters.Select(c => c.Id).ToList();

        // Find and handle all upscale and split-related tasks for the chapters being merged
        string chapterIdsJson = JsonSerializer.Serialize(chapterIds);
        string taskTypesJson = JsonSerializer.Serialize(ChapterScopedTaskTypes);

        List<PersistedTask> allRelatedTasks = await dbContext.PersistedTasks
            .FromSql($"""
                          SELECT * FROM PersistedTasks 
                          WHERE Data->>'$.ChapterId' IN (SELECT value FROM json_each({chapterIdsJson})) 
                            AND Data->>'$.$type' IN (SELECT value FROM json_each({taskTypesJson}))
                      """)
            .OrderBy(p => p.Status == PersistedTaskStatus.Pending ? 0 : 1)
            .ToListAsync(cancellationToken);

        // Process tasks based on their status
        var tasksToRemove = new List<PersistedTask>();
        var tasksToCancel = new List<PersistedTask>();

        foreach (PersistedTask task in allRelatedTasks)
        {
            int? taskChapterId = GetChapterId(task.Data);
            string taskTypeName = task.Data.GetType().Name;

            switch (task.Status)
            {
                case PersistedTaskStatus.Pending:
                    // Remove pending tasks from the queue
                    upscaleTaskProcessor.CancelCurrent(task);
                    await taskQueue.RemoveTaskAsync(task);
                    logger.LogInformation(
                        "Removed pending {TaskType} task for chapter {ChapterId} due to chapter merging",
                        taskTypeName,
                        taskChapterId
                    );
                    break;

                case PersistedTaskStatus.Processing:
                    // Cancel running tasks using the processor's cancellation mechanism
                    upscaleTaskProcessor.CancelCurrent(task);
                    tasksToCancel.Add(task);
                    logger.LogInformation(
                        "Canceled processing {TaskType} task for chapter {ChapterId} due to chapter merging",
                        taskTypeName,
                        taskChapterId
                    );
                    break;

                case PersistedTaskStatus.Completed:
                    // Remove completed tasks from database
                    tasksToRemove.Add(task);
                    logger.LogDebug(
                        "Removing completed {TaskType} task for chapter {ChapterId} due to chapter merging",
                        taskTypeName,
                        taskChapterId
                    );
                    break;

                case PersistedTaskStatus.Failed:
                case PersistedTaskStatus.Canceled:
                    // Remove failed/canceled tasks from database
                    tasksToRemove.Add(task);
                    logger.LogDebug(
                        "Removing {Status} {TaskType} task for chapter {ChapterId} due to chapter merging",
                        task.Status,
                        taskTypeName,
                        taskChapterId
                    );
                    break;
            }
        }

        // Wait a moment for canceled tasks to be processed and update their status
        if (tasksToCancel.Count != 0)
        {
            // Give the processor a moment to handle the cancellation
            await Task.Delay(100, cancellationToken);

            // Refresh the task status from database to see if they were properly canceled
            foreach (PersistedTask canceledTask in tasksToCancel)
            {
                bool taskStillExists = true;
                try
                {
                    await dbContext.Entry(canceledTask).ReloadAsync(cancellationToken);
                }
                catch
                {
                    // If task was already deleted, skip status check and removal
                    taskStillExists = false;
                    logger.LogDebug(
                        "Task {TaskId} ({TaskType}) already removed from database during cancellation wait",
                        canceledTask.Id,
                        canceledTask.Data.GetType().Name
                    );
                }

                if (!taskStillExists)
                {
                    continue;
                }

                if (canceledTask.Status != PersistedTaskStatus.Canceled)
                {
                    upscaleTaskProcessor.CancelCurrent(canceledTask);
                }

                // Add to removal list since it was successfully canceled
                tasksToRemove.Add(canceledTask);
                logger.LogDebug(
                    "Successfully canceled and will remove task {TaskType} for chapter {ChapterId}",
                    canceledTask.Data.GetType().Name,
                    GetChapterId(canceledTask.Data)
                );
            }
        }

        // Remove all tasks that should be cleaned up (completed, failed, canceled, and successfully canceled)
        if (tasksToRemove.Count != 0)
        {
            foreach (PersistedTask persistedTask in tasksToRemove)
            {
                try
                {
                    await taskQueue.RemoveTaskAsync(persistedTask);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Ignore if task was already removed by another process
                    logger.LogDebug(
                        "Task {TaskId} already removed by another process, skipping",
                        persistedTask.Id
                    );
                }
            }

            logger.LogInformation(
                "Removed {TaskCount} tasks for chapter merging cleanup",
                tasksToRemove.Count
            );
        }

        // If library has upscaling enabled and merged chapter should be upscaled,
        // queue an appropriate task for the merged chapter (full upscale or repair)
        await QueueUpscaleTaskForMergedChapterIfNeeded(
            originalChapters,
            mergeInfo,
            library,
            upscaledMergeResult,
            cancellationToken
        );
    }

    public async Task<UpscaleCompatibilityResult> CheckUpscaleCompatibilityForMergeAsync(
        List<Chapter> chapters,
        CancellationToken cancellationToken = default
    )
    {
        // Check if any chapters have pending or in-progress upscale or split tasks for logging purposes
        List<int> chapterIds = chapters.Select(c => c.Id).ToList();

        string chapterIdsJson = JsonSerializer.Serialize(chapterIds);
        string taskTypesJson = JsonSerializer.Serialize(ChapterScopedTaskTypes);

        List<PersistedTask> pendingTasks = await dbContext.PersistedTasks
            .FromSql($"""
                          SELECT * FROM PersistedTasks 
                          WHERE Data->>'$.ChapterId' IN (SELECT value FROM json_each({chapterIdsJson})) 
                            AND Data->>'$.$type' IN (SELECT value FROM json_each({taskTypesJson}))
                            AND Status IN ({(int)PersistedTaskStatus.Pending}, {(int)PersistedTaskStatus.Processing})
                      """)
            .ToListAsync(cancellationToken);

        if (pendingTasks.Any())
        {
            logger.LogInformation(
                "Merging chapters with pending/processing tasks - these will be canceled/removed: {ChapterNames}",
                string.Join(
                    ", ",
                    chapters
                        .Where(c => pendingTasks.Any(pt => GetChapterId(pt.Data) == c.Id))
                        .Select(c => c.FileName)
                )
            );
        }

        // Always allow merging since we now handle task management properly
        return new UpscaleCompatibilityResult(true);
    }

    private static int? GetChapterId(BaseTask task)
    {
        return task switch
        {
            UpscaleTask t => t.ChapterId,
            RepairUpscaleTask t => t.ChapterId,
            RenameUpscaledChaptersSeriesTask t => t.ChapterId,
            DetectSplitCandidatesTask t => t.ChapterId,
            ApplySplitsTask t => t.ChapterId,
            _ => null,
        };
    }

    private async Task QueueUpscaleTaskForMergedChapterIfNeeded(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        UpscaledMergeResult? upscaledMergeResult,
        CancellationToken cancellationToken
    )
    {
        Chapter primaryChapter = originalChapters.First();
        await dbContext.Entry(primaryChapter).Reference(x => x.Manga).LoadAsync(cancellationToken);
        await dbContext
            .Entry(primaryChapter.Manga)
            .Reference(x => x.Library)
            .LoadAsync(cancellationToken);
        await dbContext
            .Entry(primaryChapter.Manga.Library)
            .Reference(x => x.UpscalerProfile)
            .LoadAsync(cancellationToken);

        // Check if strip detection is needed for the merged chapter
        if (library.StripDetectionMode != StripDetectionMode.None)
        {
            bool needsSplitDetection = await splitProcessingCoordinator.ShouldProcessAsync(
                primaryChapter.Id,
                library.StripDetectionMode,
                dbContext,
                cancellationToken
            );

            if (needsSplitDetection)
            {
                await splitProcessingCoordinator.EnqueueDetectionAsync(
                    primaryChapter.Id,
                    cancellationToken
                );
                logger.LogInformation(
                    "Queued split detection for merged chapter {FileName} (Chapter ID: {ChapterId}) instead of immediate upscaling",
                    mergeInfo.MergedChapter.FileName,
                    primaryChapter.Id
                );
                return; // Defer upscaling until split detection is complete
            }
        }

        if (string.IsNullOrEmpty(library.UpscaledLibraryPath) || library.UpscalerProfile is null)
        {
            return;
        }

        bool shouldUpscale =
            (primaryChapter.Manga.ShouldUpscale ?? primaryChapter.Manga.Library.UpscaleOnIngest)
            && primaryChapter.Manga.Library.UpscalerProfile != null;

        if (shouldUpscale)
        {
            // Check if this is a partial upscaling scenario based on the merge result
            if (upscaledMergeResult?.IsPartialMerge == true)
            {
                // Queue a repair task to handle the missing pages
                var repairTask = new RepairUpscaleTask(
                    primaryChapter,
                    primaryChapter.Manga.Library.UpscalerProfile!
                );
                await taskQueue.EnqueueAsync(repairTask);

                logger.LogInformation(
                    "Queued repair upscale task for partially merged chapter {FileName} (Chapter ID: {ChapterId}) "
                        + "to complete {MissingCount} missing pages from {ExistingCount} existing upscaled parts",
                    mergeInfo.MergedChapter.FileName,
                    primaryChapter.Id,
                    upscaledMergeResult.MissingPartsCount,
                    upscaledMergeResult.UpscaledPartsCount
                );
            }
            else if (upscaledMergeResult?.HasUpscaledContent == true)
            {
                // Complete merge already happened, no task needed
                logger.LogDebug(
                    "Complete upscaled merge already processed for chapter {FileName}, no additional task needed",
                    mergeInfo.MergedChapter.FileName
                );
            }
            else
            {
                // Queue a full upscale task for normal scenarios (no existing upscaled content)
                var upscaleTask = new UpscaleTask(primaryChapter);
                await taskQueue.EnqueueAsync(upscaleTask);

                logger.LogInformation(
                    "Queued full upscale task for merged chapter {FileName} (Chapter ID: {ChapterId})",
                    mergeInfo.MergedChapter.FileName,
                    primaryChapter.Id
                );
            }
        }
        else
        {
            logger.LogDebug(
                "Upscale task for merged chapter {FileName} skipped - upscaling not needed",
                mergeInfo.MergedChapter.FileName
            );
        }
    }
}
