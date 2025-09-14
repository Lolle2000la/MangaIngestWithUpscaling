using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Threading.Channels;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class DistributedUpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<UpscalerConfig> upscalerConfig) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly ChannelReader<PersistedTask> _reader = taskQueue.UpscaleReader;

    private readonly Channel<(TaskCompletionSource<PersistedTask>, CancellationToken)> _taskRequests =
        Channel.CreateUnbounded<(TaskCompletionSource<PersistedTask>, CancellationToken)>();

    private readonly Dictionary<int, PersistedTask> runningTasks = new();
    private CancellationToken serviceStoppingToken;

    public event Func<PersistedTask, Task>? StatusChanged;

    /// <summary>
    /// Cancels the current task if it matches the given task.
    /// The task is necessary to prevent canceling another if the task has already been processed.
    /// Otherwise, consistency issues may arise.
    /// </summary>
    /// <param name="checkAgainst">The task to check against if it is still the current task. Does so by using the Id.</param>
    public async Task CancelCurrent(PersistedTask checkAgainst)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        using (_lock.EnterScope())
        {
            if (runningTasks.TryGetValue(checkAgainst.Id, out var currentTask) && currentTask.Id == checkAgainst.Id)
            {
                currentTask.Status = PersistedTaskStatus.Canceled;
                _ = StatusChanged?.Invoke(currentTask);

                runningTasks.Remove(checkAgainst.Id);
            }
            else
            {
                return;
            }
        }


        PersistedTask? task = await dbContext.PersistedTasks.FirstOrDefaultAsync(t => t.Id == checkAgainst.Id);
        if (task == null)
        {
            return; // Task not found in the database, nothing to do
        }

        task.Status = PersistedTaskStatus.Canceled;
        await dbContext.SaveChangesAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;

        _ = Task.Run(async () =>
        {
            var cleanDeadTasksTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (!stoppingToken.IsCancellationRequested &&
                   await cleanDeadTasksTimer.WaitForNextTickAsync(stoppingToken))
            {
                List<PersistedTask> deadTasksToRequeue;
                using (_lock.EnterScope())
                {
                    var deadTasks = runningTasks.Where(x =>
                            x.Value.Status == PersistedTaskStatus.Processing
                            && x.Value.LastKeepAlive.AddMinutes(1) < DateTime.UtcNow)
                        .ToList();

                    if (deadTasks.Count == 0)
                    {
                        continue;
                    }

                    deadTasksToRequeue = new List<PersistedTask>(deadTasks.Count);
                    foreach (var (taskId, task) in deadTasks)
                    {
                        deadTasksToRequeue.Add(task);
                        runningTasks.Remove(taskId);
                    }
                }

                using (IServiceScope scope = scopeFactory.CreateScope())
                {
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
                    logger.LogInformation("Re-enqueuing {count} dead tasks.", deadTasksToRequeue.Count);
                }

                foreach (PersistedTask task in deadTasksToRequeue)
                {
                    using IServiceScope scope2 = scopeFactory.CreateScope();
                    var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    // Re-check the current status in DB to avoid re-enqueueing tasks that already completed or were cancelled
                    PersistedTask? current = await db2.PersistedTasks.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == task.Id, stoppingToken);
                    if (current is null)
                    {
                        continue;
                    }

                    if (current.Status == PersistedTaskStatus.Completed ||
                        current.Status == PersistedTaskStatus.Canceled)
                    {
                        // Already finalized; do not re-enqueue
                        continue;
                    }

                    await taskQueue.RetryAsync(task);
                    _ = StatusChanged?.Invoke(task);
                }
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            (TaskCompletionSource<PersistedTask> tcs, CancellationToken cancelToken) =
                await _taskRequests.Reader.ReadAsync(stoppingToken);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancelToken);
            using CancellationTokenRegistration registration = linkedCts.Token.Register(() => tcs.TrySetCanceled());
            if (tcs.Task.IsCanceled)
            {
                continue;
            }

            try
            {
                bool completed = false;
                while (!completed && !stoppingToken.IsCancellationRequested)
                {
                    PersistedTask task = await _reader.ReadAsync(linkedCts.Token);

                    using (IServiceScope scope = scopeFactory.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var logger =
                            scope.ServiceProvider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();

                        PersistedTask? taskFromDb = await dbContext.PersistedTasks.AsNoTracking()
                            .FirstOrDefaultAsync(t => t.Id == task.Id, linkedCts.Token);

                        // A task should only be processed if it's in the "Pending" state.
                        // If it's anything else, another worker has claimed it or it's finalized.
                        if (taskFromDb == null || taskFromDb.Status != PersistedTaskStatus.Pending)
                        {
                            if (taskFromDb != null)
                            {
                                logger.LogInformation(
                                    "Skipping task {taskId} as it is no longer pending (current status: {status}).",
                                    task.Id, taskFromDb.Status);
                            }

                            continue; // Skip this task and try to get the next one from the channel.
                        }
                    }

                    // Handle RepairUpscaleTask specially based on remote-only mode
                    if (task.Data is RepairUpscaleTask repairTask)
                    {
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger = scope.ServiceProvider
                            .GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();

                        if (upscalerConfig.Value.RemoteOnly)
                        {
                            // In remote-only mode, handle RepairUpscaleTask locally with remote upscaling
                            logger.LogDebug(
                                "Processing RepairUpscaleTask {taskId} in remote-only mode using distributed repair workflow.",
                                task.Id);

                            await ProcessRemoteRepairTask(repairTask, task, scope.ServiceProvider, linkedCts.Token);
                            continue;
                        }
                        else
                        {
                            // In local mode, reroute to UpscaleTaskProcessor as before
                            logger.LogDebug(
                                "Rerouting RepairUpscaleTask {taskId} to local UpscaleTaskProcessor for local processing.",
                                task.Id);

                            await taskQueue.SendToLocalUpscaleAsync(task, linkedCts.Token);
                            continue;
                        }
                    }

                    // Handle other reroutable tasks (e.g., RenameUpscaledChaptersSeriesTask)
                    if (task.Data is RenameUpscaledChaptersSeriesTask)
                    {
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger = scope.ServiceProvider
                            .GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
                        logger.LogDebug(
                            "Rerouting task {taskId} ({taskType}) to local UpscaleTaskProcessor and continuing to search.",
                            task.Id, task.Data.GetType().Name);

                        await taskQueue.SendToLocalUpscaleAsync(task, linkedCts.Token);
                        continue;
                    }

                    if (task.Data is UpscaleTask upscaleData)
                    {
                        // Check if the target chapter file still exists before giving the task to the worker
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger =
                            scope.ServiceProvider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        Chapter? chapter = await dbContext.Chapters
                            .Include(t => t.Manga)
                            .ThenInclude(t => t.Library)
                            .ThenInclude(t => t.UpscalerProfile)
                            .Include(t => t.UpscalerProfile)
                            .FirstOrDefaultAsync(c => c.Id == upscaleData.ChapterId, linkedCts.Token);
                        if (chapter == null || !File.Exists(chapter.NotUpscaledFullPath))
                        {
                            // Chapter no longer exists, mark task as failed
                            task.Status = PersistedTaskStatus.Failed;
                            task.ProcessedAt = DateTime.UtcNow;
                            _ = StatusChanged?.Invoke(task);

                            logger.LogWarning("Skipping task {taskId} because chapter file is missing.", task.Id);
                            continue;
                        }

                        // check if it is already upscaled
                        if (chapter.IsUpscaled)
                        {
                            task.Status = PersistedTaskStatus.Completed;
                            task.ProcessedAt = DateTime.UtcNow;
                            _ = StatusChanged?.Invoke(task);
                            logger.LogInformation("Skipping task {taskId} because chapter is already upscaled.",
                                task.Id);
                            continue;
                        }

                        // check if the target file already exists and has equal pages
                        if (File.Exists(chapter.UpscaledFullPath))
                        {
                            var metadataHandling =
                                scope.ServiceProvider.GetRequiredService<IMetadataHandlingService>();
                            if (metadataHandling.PagesEqual(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath))
                            {
                                task.Status = PersistedTaskStatus.Completed;
                                task.ProcessedAt = DateTime.UtcNow;
                                chapter.IsUpscaled = true;
                                _ = StatusChanged?.Invoke(task);
                                logger.LogInformation(
                                    "Skipping task {taskId} because target file already exists and is equal.",
                                    task.Id);
                                continue;
                            }
                        }
                    }

                    if (!tcs.TrySetResult(task))
                    {
                        // Requester couldn't accept the task (likely cancelled) — re-enqueue immediately
                        await taskQueue.RetryAsync(task);
                        _ = StatusChanged?.Invoke(task);
                    }

                    completed = true; // Task has been successfully passed or re-enqueued.
                }
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(linkedCts.Token);
                break;
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }

    public async Task<PersistedTask?> GetTask(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource<PersistedTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _taskRequests.Writer.WriteAsync((tcs, stoppingToken), stoppingToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
        using CancellationTokenRegistration registration = linkedCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            PersistedTask task = await tcs.Task;

            //
            // Now that we have a claimed task, update its status in the DB to "Processing"
            //
            using (IServiceScope scope = scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                PersistedTask? taskFromDb =
                    await dbContext.PersistedTasks.FirstOrDefaultAsync(t => t.Id == task.Id, stoppingToken);
                if (taskFromDb != null)
                {
                    taskFromDb.Status = PersistedTaskStatus.Processing;
                    await dbContext.SaveChangesAsync(stoppingToken);
                }
            }

            task.Status = PersistedTaskStatus.Processing;
            task.LastKeepAlive = DateTime.UtcNow.AddSeconds(5); // Bridge network latency

            using (_lock.EnterScope())
            {
                runningTasks[task.Id] = task;
            }

            _ = StatusChanged?.Invoke(task);
            return task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public bool KeepAlive(int taskId)
    {
        using (_lock.EnterScope())
        {
            if (runningTasks.TryGetValue(taskId, out var currentTask))
            {
                currentTask.LastKeepAlive = DateTime.UtcNow;
                // Notify listeners (e.g., TaskRegistry/UI) that the task heartbeat was updated
                _ = StatusChanged?.Invoke(currentTask);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Applies progress updates coming from a remote worker to the running task, if any.
    ///     Backward-compatible usage via optional fields: only provided values are applied.
    /// </summary>
    public void ApplyProgress(int taskId, int? total, int? current, string? statusMessage, string? phase)
    {
        using (_lock.EnterScope())
        {
            if (runningTasks.TryGetValue(taskId, out PersistedTask? task))
            {
                ProgressInfo p = task.Data.Progress;
                if (total.HasValue)
                {
                    p.Total = total.Value;
                }

                if (current.HasValue)
                {
                    p.Current = current.Value;
                }

                if (!string.IsNullOrWhiteSpace(statusMessage))
                {
                    p.StatusMessage = statusMessage!;
                }
                else if (!string.IsNullOrWhiteSpace(phase))
                {
                    // Use phase as a fallback status message for visibility
                    p.StatusMessage = phase!;
                }

                // Treat progress updates as heartbeats as well, to keep liveness fresh
                task.LastKeepAlive = DateTime.UtcNow;

                _ = StatusChanged?.Invoke(task);
            }
        }
    }

    public async Task TaskCompleted(int taskId)
    {
        DateTime time = DateTime.UtcNow;
        using (_lock.EnterScope())
        {
            // No orphan buffer to clear (re-enqueued immediately on delivery failure)

            if (runningTasks.TryGetValue(taskId, out PersistedTask? task))
            {
                task.ProcessedAt = time;
                task.Status = PersistedTaskStatus.Completed;
                _ = StatusChanged?.Invoke(task);
                runningTasks.Remove(taskId);
            }
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? localTask = await dbContext.PersistedTasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (localTask == null)
        {
            return; // Task not found in the database, nothing to do
        }

        localTask.ProcessedAt = time;
        localTask.Status = PersistedTaskStatus.Completed;
        await dbContext.SaveChangesAsync();
    }

    public async Task TaskFailed(int taskId, string? errorMessage)
    {
        using (IServiceScope scope = scopeFactory.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
            if (!string.IsNullOrEmpty(errorMessage))
            {
                logger.LogWarning("Task {taskId} failed on remote worker: {errorMessage}", taskId, errorMessage);
            }
        }

        using (_lock.EnterScope())
        {
            if (runningTasks.TryGetValue(taskId, out PersistedTask? task))
            {
                task.Status = PersistedTaskStatus.Failed;
                runningTasks.Remove(taskId);
                _ = StatusChanged?.Invoke(task);
            }
        }

        using IServiceScope dbScope = scopeFactory.CreateScope();
        var dbContext = dbScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? localTask = await dbContext.PersistedTasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (localTask == null)
        {
            return; // Task not found in the database, nothing to do
        }

        localTask.Status = PersistedTaskStatus.Failed;
        localTask.RetryCount++;
        await dbContext.SaveChangesAsync();
        _ = StatusChanged?.Invoke(localTask);
    }

    /// <summary>
    /// Processes a RepairUpscaleTask in remote-only mode by preparing missing pages,
    /// sending them to remote workers for upscaling, and merging the results back.
    /// </summary>
    private async Task ProcessRemoteRepairTask(
        RepairUpscaleTask repairTask,
        PersistedTask persistedTask,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Update task status to processing
            persistedTask.Status = PersistedTaskStatus.Processing;
            dbContext.Update(persistedTask);
            await dbContext.SaveChangesAsync(cancellationToken);
            _ = StatusChanged?.Invoke(persistedTask);

            // Load chapter and upscaler profile
            Chapter? chapter = await dbContext.Chapters
                .Include(c => c.Manga)
                .ThenInclude(m => m.Library)
                .ThenInclude(l => l.UpscalerProfile)
                .Include(c => c.UpscalerProfile)
                .FirstOrDefaultAsync(c => c.Id == repairTask.ChapterId, cancellationToken);
            
            UpscalerProfile? upscalerProfile = chapter?.UpscalerProfile ??
                                               await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
                                                   c => c.Id == repairTask.UpscalerProfileId, cancellationToken);

            if (chapter == null || upscalerProfile == null)
            {
                throw new InvalidOperationException(
                    $"Chapter ({chapter?.RelativePath ?? "Not found"}) or upscaler profile ({upscalerProfile?.Name ?? "Not found"}, id: {repairTask.UpscalerProfileId}) not found.");
            }

            if (chapter.UpscaledFullPath == null)
            {
                throw new InvalidOperationException(
                    $"Upscaled library path of library {chapter.Manga?.Library?.Name ?? "Unknown"} ({chapter.Manga?.Library?.Id}) not set.");
            }

            string upscaleTargetPath = chapter.UpscaledFullPath;
            string currentStoragePath = chapter.NotUpscaledFullPath;

            logger.LogInformation("Starting remote repair of chapter \"{chapterFileName}\" of {seriesTitle}",
                chapter.FileName, chapter.Manga.PrimaryTitle);

            var metadataHandling = services.GetRequiredService<IMetadataHandlingService>();
            var differences = metadataHandling.AnalyzePageDifferences(currentStoragePath, upscaleTargetPath);

            if (differences.AreEqual)
            {
                logger.LogInformation("Chapter \"{chapterFileName}\" of {seriesTitle} no longer needs repair",
                    chapter.FileName, chapter.Manga.PrimaryTitle);
                
                persistedTask.Status = PersistedTaskStatus.Completed;
                persistedTask.ProcessedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _ = StatusChanged?.Invoke(persistedTask);
                return;
            }

            if (!differences.CanRepair)
            {
                logger.LogWarning(
                    "Chapter \"{chapterFileName}\" of {seriesTitle} cannot be repaired - will fall back to full re-upscale",
                    chapter.FileName, chapter.Manga.PrimaryTitle);

                // Fall back to full upscale by creating a regular UpscaleTask and enqueuing it
                var fallbackTask = new UpscaleTask(chapter, upscalerProfile);
                await taskQueue.EnqueueAsync(fallbackTask);
                
                // Mark original repair task as completed since we've handled the fallback
                persistedTask.Status = PersistedTaskStatus.Completed;
                persistedTask.ProcessedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _ = StatusChanged?.Invoke(persistedTask);
                return;
            }

            // Perform the remote repair workflow
            var repairService = services.GetRequiredService<IRepairService>();
            await PerformRemoteRepair(
                chapter, upscalerProfile, differences, currentStoragePath, upscaleTargetPath,
                metadataHandling, repairService, services, persistedTask, logger, cancellationToken);

            // Reload chapter and manga from db in case title changed
            await dbContext.Entry(chapter).ReloadAsync();
            await dbContext.Entry(chapter.Manga).ReloadAsync();

            // Apply any title changes
            var metadataChanger = services.GetRequiredService<IMangaMetadataChanger>();
            metadataChanger.ApplyMangaTitleToUpscaled(chapter, chapter.Manga.PrimaryTitle, upscaleTargetPath);

            // Mark task as completed
            persistedTask.Status = PersistedTaskStatus.Completed;
            persistedTask.ProcessedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            _ = StatusChanged?.Invoke(persistedTask);

            // Notify that chapter was changed
            var chapterChangedNotifier = services.GetRequiredService<IChapterChangedNotifier>();
            _ = chapterChangedNotifier.Notify(chapter, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remote repair task {TaskId} failed", persistedTask.Id);
            persistedTask.Status = PersistedTaskStatus.Failed;
            persistedTask.RetryCount++;
            try
            {
                await dbContext.SaveChangesAsync();
                _ = StatusChanged?.Invoke(persistedTask);
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to update task {TaskId} status", persistedTask.Id);
            }
        }
    }

    /// <summary>
    /// Performs the remote repair workflow by preparing missing pages, upscaling them remotely,
    /// and merging the results back into the original CBZ.
    /// </summary>
    private async Task PerformRemoteRepair(
        Chapter chapter,
        UpscalerProfile profile,
        PageDifferenceResult differences,
        string originalPath,
        string upscaledPath,
        IMetadataHandlingService metadataHandling,
        IRepairService repairService,
        IServiceProvider services,
        PersistedTask persistedTask,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var repairContext = repairService.PrepareRepairContext(differences, originalPath, upscaledPath, logger);
        
        try
        {
            persistedTask.Data.Progress.StatusMessage = "Preparing missing pages for remote upscaling";
            _ = StatusChanged?.Invoke(persistedTask);

            if (differences.MissingPages.Count > 0)
            {
                persistedTask.Data.Progress.StatusMessage = "Upscaling missing pages remotely";
                persistedTask.Data.Progress.Total = differences.MissingPages.Count;
                persistedTask.Data.Progress.Current = 0;
                persistedTask.Data.Progress.ProgressUnit = "pages";
                _ = StatusChanged?.Invoke(persistedTask);

                // Get the upscaler from DI - this will handle remote delegation automatically
                var upscaler = services.GetRequiredService<IUpscaler>();

                // This will be processed by the remote worker
                await upscaler.Upscale(repairContext.MissingPagesCbz, repairContext.UpscaledMissingCbz, profile, cancellationToken);
            }

            persistedTask.Data.Progress.StatusMessage = "Merging repaired pages";
            _ = StatusChanged?.Invoke(persistedTask);

            // Merge the results back using the shared repair service
            repairService.MergeRepairResults(repairContext, upscaledPath, logger);

            logger.LogInformation(
                "Successfully repaired chapter remotely with {missingCount} missing pages and {extraCount} extra pages removed",
                differences.MissingPages.Count, differences.ExtraPages.Count);
        }
        finally
        {
            repairContext.Dispose();
        }
    }
}