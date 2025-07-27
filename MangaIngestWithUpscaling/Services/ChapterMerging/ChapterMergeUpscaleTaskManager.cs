using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeUpscaleTaskManager(
    ApplicationDbContext dbContext,
    ITaskQueue taskQueue,
    UpscaleTaskProcessor upscaleTaskProcessor,
    ILogger<ChapterMergeUpscaleTaskManager> logger) : IChapterMergeUpscaleTaskManager
{
    public async Task HandleUpscaleTaskManagementAsync(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        CancellationToken cancellationToken = default)
    {
        List<int> chapterIds = originalChapters.Select(c => c.Id).ToList();

        // Find and handle all upscale tasks for the chapters being merged
        var allRelatedTasks = new List<PersistedTask>();

        // Use raw SQL to query JSON data for each chapter ID to get all related tasks
        foreach (int chapterId in chapterIds)
        {
            List<PersistedTask> tasks = (await dbContext.PersistedTasks
                    .FromSql(
                        $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapterId}")
                    .ToListAsync(cancellationToken))
                .OrderBy(p => p.Status == PersistedTaskStatus.Pending ? 0 : 1)
                .ToList();

            allRelatedTasks.AddRange(tasks);
        }

        // Process tasks based on their status
        var tasksToRemove = new List<PersistedTask>();
        var tasksToCancel = new List<PersistedTask>();

        foreach (PersistedTask task in allRelatedTasks)
        {
            switch (task.Status)
            {
                case PersistedTaskStatus.Pending:
                    // Remove pending tasks from the queue
                    upscaleTaskProcessor.CancelCurrent(task);
                    await taskQueue.RemoveTaskAsync(task);
                    logger.LogInformation("Removed pending upscale task for chapter {ChapterId} due to chapter merging",
                        ((UpscaleTask)task.Data).ChapterId);
                    break;

                case PersistedTaskStatus.Processing:
                    // Cancel running tasks using the processor's cancellation mechanism
                    upscaleTaskProcessor.CancelCurrent(task);
                    tasksToCancel.Add(task);
                    logger.LogInformation(
                        "Canceled processing upscale task for chapter {ChapterId} due to chapter merging",
                        ((UpscaleTask)task.Data).ChapterId);
                    break;

                case PersistedTaskStatus.Completed:
                    // Remove completed tasks from database
                    tasksToRemove.Add(task);
                    logger.LogDebug("Removing completed upscale task for chapter {ChapterId} due to chapter merging",
                        ((UpscaleTask)task.Data).ChapterId);
                    break;

                case PersistedTaskStatus.Failed:
                case PersistedTaskStatus.Canceled:
                    // Remove failed/canceled tasks from database
                    tasksToRemove.Add(task);
                    logger.LogDebug("Removing {Status} upscale task for chapter {ChapterId} due to chapter merging",
                        task.Status, ((UpscaleTask)task.Data).ChapterId);
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
                await dbContext.Entry(canceledTask).ReloadAsync(cancellationToken);
                if (canceledTask.Status != PersistedTaskStatus.Canceled)
                {
                    upscaleTaskProcessor.CancelCurrent(canceledTask);
                }

                // Add to removal list since it was successfully canceled
                tasksToRemove.Add(canceledTask);
                logger.LogDebug("Successfully canceled and will remove task for chapter {ChapterId}",
                    ((UpscaleTask)canceledTask.Data).ChapterId);
            }
        }

        // Remove all tasks that should be cleaned up (completed, failed, canceled, and successfully canceled)
        if (tasksToRemove.Count != 0)
        {
            foreach (PersistedTask persistedTask in tasksToRemove)
            {
                await taskQueue.RemoveTaskAsync(persistedTask);
            }

            logger.LogInformation("Removed {TaskCount} upscale tasks for chapter merging cleanup", tasksToRemove.Count);
        }

        // If library has upscaling enabled and merged chapter should be upscaled,
        // queue an upscale task for the merged chapter
        await QueueUpscaleTaskForMergedChapterIfNeeded(originalChapters, mergeInfo, library, cancellationToken);
    }

    public async Task<UpscaleCompatibilityResult> CheckUpscaleCompatibilityForMergeAsync(
        List<Chapter> chapters,
        CancellationToken cancellationToken = default)
    {
        // Check if any chapters have pending or in-progress upscale tasks for logging purposes
        List<int> chapterIds = chapters.Select(c => c.Id).ToList();

        var pendingTasks = new List<PersistedTask>();

        // Use raw SQL to query JSON data for each chapter ID
        foreach (int chapterId in chapterIds)
        {
            List<PersistedTask> tasks = await dbContext.PersistedTasks
                .FromSql(
                    $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapterId} AND (Status = {(int)PersistedTaskStatus.Pending} OR Status = {(int)PersistedTaskStatus.Processing})")
                .ToListAsync(cancellationToken);

            pendingTasks.AddRange(tasks);
        }

        if (pendingTasks.Any())
        {
            // Extract chapter IDs from the task data for comparison
            HashSet<int> taskChapterIds = pendingTasks
                .Where(pt => pt.Data is UpscaleTask)
                .Select(pt => ((UpscaleTask)pt.Data).ChapterId)
                .ToHashSet();

            List<Chapter> affectedChapters = chapters.Where(c => taskChapterIds.Contains(c.Id)).ToList();

            logger.LogInformation(
                "Merging chapters with pending/processing upscale tasks - these will be canceled/removed: {ChapterNames}",
                string.Join(", ", affectedChapters.Select(c => c.FileName)));
        }

        // Always allow merging since we now handle task management properly
        return new UpscaleCompatibilityResult(true);
    }

    private async Task QueueUpscaleTaskForMergedChapterIfNeeded(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(library.UpscaledLibraryPath) || library.UpscalerProfile is null)
        {
            return;
        }

        Chapter primaryChapter = originalChapters.First();
        await dbContext.Entry(primaryChapter).Reference(x => x.Manga).LoadAsync(cancellationToken);
        await dbContext.Entry(primaryChapter.Manga).Reference(x => x.Library).LoadAsync(cancellationToken);
        await dbContext.Entry(primaryChapter.Manga.Library).Reference(x => x.UpscalerProfile)
            .LoadAsync(cancellationToken);

        // Check if there's already an upscale task for the merged chapter
        bool shouldUpscale = (primaryChapter.Manga.ShouldUpscale ?? primaryChapter.Manga.Library.UpscaleOnIngest)
                             && primaryChapter.Manga.Library.UpscalerProfile != null;

        if (shouldUpscale)
        {
            var upscaleTask = new UpscaleTask(primaryChapter);

            await taskQueue.EnqueueAsync(upscaleTask);

            logger.LogInformation(
                "Queued replacement upscale task for merged chapter {FileName} (Chapter ID: {ChapterId})",
                mergeInfo.MergedChapter.FileName, primaryChapter.Id);
        }
        else
        {
            logger.LogDebug("Upscale task for merged chapter {FileName} skipped - upscaling not needed",
                mergeInfo.MergedChapter.FileName);
        }
    }
}