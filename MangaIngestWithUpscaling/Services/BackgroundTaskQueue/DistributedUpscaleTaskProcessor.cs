using System.Threading.Channels;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class DistributedUpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<UpscalerConfig> upscalerConfig
) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly ChannelReader<PersistedTask> _reader = taskQueue.UpscaleReader;

    private readonly Channel<(
        TaskCompletionSource<PersistedTask>,
        CancellationToken
    )> _taskRequests = Channel.CreateUnbounded<(
        TaskCompletionSource<PersistedTask>,
        CancellationToken
    )>();

    // Store remote repair state separately from the task itself
    private readonly Dictionary<int, RemoteRepairState> remoteRepairStates = new();

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
            if (
                runningTasks.TryGetValue(checkAgainst.Id, out var currentTask)
                && currentTask.Id == checkAgainst.Id
            )
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

        PersistedTask? task = await dbContext.PersistedTasks.FirstOrDefaultAsync(t =>
            t.Id == checkAgainst.Id
        );
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

        _ = Task.Run(
            async () =>
            {
                var cleanDeadTasksTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (
                    !stoppingToken.IsCancellationRequested
                    && await cleanDeadTasksTimer.WaitForNextTickAsync(stoppingToken)
                )
                {
                    List<PersistedTask> deadTasksToRequeue;
                    using (_lock.EnterScope())
                    {
                        var deadTasks = runningTasks
                            .Where(x =>
                                x.Value.Status == PersistedTaskStatus.Processing
                                && x.Value.LastKeepAlive.AddMinutes(1) < DateTime.UtcNow
                            )
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
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();
                        logger.LogInformation(
                            "Re-enqueuing {count} dead tasks.",
                            deadTasksToRequeue.Count
                        );
                    }

                    foreach (PersistedTask task in deadTasksToRequeue)
                    {
                        using IServiceScope scope2 = scopeFactory.CreateScope();
                        var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        // Re-check the current status in DB to avoid re-enqueueing tasks that already completed or were cancelled
                        PersistedTask? current = await db2
                            .PersistedTasks.AsNoTracking()
                            .FirstOrDefaultAsync(t => t.Id == task.Id, stoppingToken);
                        if (current is null)
                        {
                            continue;
                        }

                        if (
                            current.Status == PersistedTaskStatus.Completed
                            || current.Status == PersistedTaskStatus.Canceled
                        )
                        {
                            // Already finalized; do not re-enqueue
                            continue;
                        }

                        await taskQueue.RetryAsync(task);
                        _ = StatusChanged?.Invoke(task);
                    }
                }
            },
            stoppingToken
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            (TaskCompletionSource<PersistedTask> tcs, CancellationToken cancelToken) =
                await _taskRequests.Reader.ReadAsync(stoppingToken);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                cancelToken
            );
            await using CancellationTokenRegistration registration = linkedCts.Token.Register(() =>
                tcs.TrySetCanceled()
            );
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

                    bool taskClaimed = false;
                    using (IServiceScope scope = scopeFactory.CreateScope())
                    {
                        var dbContext =
                            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();

                        // Atomically claim the task by transitioning from Pending to Processing
                        // This prevents multiple workers from claiming the same task
                        PersistedTask? taskFromDb =
                            await dbContext.PersistedTasks.FirstOrDefaultAsync(
                                t => t.Id == task.Id,
                                linkedCts.Token
                            );

                        if (taskFromDb == null)
                        {
                            logger.LogWarning(
                                "Task {taskId} not found in database, skipping.",
                                task.Id
                            );
                            continue; // Skip this task and try to get the next one from the channel.
                        }

                        // A task should only be processed if it's in the "Pending" state.
                        // If it's anything else, another worker has claimed it or it's finalized.
                        if (taskFromDb.Status != PersistedTaskStatus.Pending)
                        {
                            logger.LogInformation(
                                "Skipping task {taskId} as it is no longer pending (current status: {status}).",
                                task.Id,
                                taskFromDb.Status
                            );
                            continue; // Skip this task and try to get the next one from the channel.
                        }

                        // Atomically transition to Processing - only one worker will succeed
                        taskFromDb.Status = PersistedTaskStatus.Processing;
                        try
                        {
                            await dbContext.SaveChangesAsync(linkedCts.Token);
                            taskClaimed = true;
                            // Update the in-memory task with the new status
                            task.Status = PersistedTaskStatus.Processing;
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            // Another worker claimed this task, skip it
                            logger.LogInformation(
                                "Task {taskId} was claimed by another worker (concurrency conflict).",
                                task.Id
                            );
                            continue;
                        }
                    }

                    // Only proceed with the task if we successfully claimed it
                    if (!taskClaimed)
                    {
                        continue;
                    }

                    // Handle RepairUpscaleTask specially based on remote-only mode
                    if (task.Data is RepairUpscaleTask repairTask)
                    {
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();

                        if (upscalerConfig.Value.RemoteOnly)
                        {
                            // In remote-only mode, prepare the repair task for remote delegation
                            logger.LogDebug(
                                "Preparing RepairUpscaleTask {taskId} for remote delegation.",
                                task.Id
                            );

                            bool prepared = await PrepareRepairTaskForRemote(
                                repairTask,
                                task,
                                scope.ServiceProvider,
                                linkedCts.Token
                            );
                            if (!prepared)
                            {
                                // If preparation failed, skip this task
                                continue;
                            }

                            // Allow the prepared repair task to be delegated to remote workers
                            // Fall through to the delegation logic below
                        }
                        else
                        {
                            // In local mode, reroute to UpscaleTaskProcessor as before
                            logger.LogDebug(
                                "Rerouting RepairUpscaleTask {taskId} to local UpscaleTaskProcessor for local processing.",
                                task.Id
                            );

                            await taskQueue.SendToLocalUpscaleAsync(task, linkedCts.Token);
                            continue;
                        }
                    }

                    // Handle other reroutable tasks (e.g., RenameUpscaledChaptersSeriesTask)
                    if (task.Data is RenameUpscaledChaptersSeriesTask)
                    {
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();
                        logger.LogDebug(
                            "Rerouting task {taskId} ({taskType}) to local UpscaleTaskProcessor and continuing to search.",
                            task.Id,
                            task.Data.GetType().Name
                        );

                        await taskQueue.SendToLocalUpscaleAsync(task, linkedCts.Token);
                        continue;
                    }

                    if (task.Data is UpscaleTask upscaleData)
                    {
                        // Check if the target chapter file still exists before giving the task to the worker
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();
                        var dbContext =
                            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        Chapter? chapter = await dbContext
                            .Chapters.Include(t => t.Manga)
                                .ThenInclude(t => t.Library)
                                    .ThenInclude(t => t.UpscalerProfile)
                            .Include(t => t.UpscalerProfile)
                            .FirstOrDefaultAsync(
                                c => c.Id == upscaleData.ChapterId,
                                linkedCts.Token
                            );
                        if (chapter == null || !File.Exists(chapter.NotUpscaledFullPath))
                        {
                            // Chapter no longer exists, mark task as failed
                            task.Status = PersistedTaskStatus.Failed;
                            task.ProcessedAt = DateTime.UtcNow;
                            _ = StatusChanged?.Invoke(task);

                            logger.LogWarning(
                                "Skipping task {taskId} because chapter file is missing.",
                                task.Id
                            );
                            continue;
                        }

                        // check if it is already upscaled
                        if (chapter.IsUpscaled)
                        {
                            task.Status = PersistedTaskStatus.Completed;
                            task.ProcessedAt = DateTime.UtcNow;
                            _ = StatusChanged?.Invoke(task);
                            logger.LogInformation(
                                "Skipping task {taskId} because chapter is already upscaled.",
                                task.Id
                            );
                            continue;
                        }

                        // check if the target file already exists and has equal pages
                        if (File.Exists(chapter.UpscaledFullPath))
                        {
                            var metadataHandling =
                                scope.ServiceProvider.GetRequiredService<IMetadataHandlingService>();
                            if (
                                await metadataHandling.PagesEqualAsync(
                                    chapter.NotUpscaledFullPath,
                                    chapter.UpscaledFullPath
                                )
                            )
                            {
                                task.Status = PersistedTaskStatus.Completed;
                                task.ProcessedAt = DateTime.UtcNow;
                                chapter.IsUpscaled = true;
                                _ = StatusChanged?.Invoke(task);
                                logger.LogInformation(
                                    "Skipping task {taskId} because target file already exists and is equal.",
                                    task.Id
                                );
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
        var tcs = new TaskCompletionSource<PersistedTask>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await _taskRequests.Writer.WriteAsync((tcs, stoppingToken), stoppingToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            timeoutCts.Token
        );
        await using CancellationTokenRegistration registration = linkedCts.Token.Register(() =>
            tcs.TrySetCanceled()
        );

        try
        {
            PersistedTask task = await tcs.Task;

            // Task status has already been atomically updated to "Processing" in ExecuteAsync
            // before it was passed to us, so we just need to track it locally
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
    public void ApplyProgress(
        int taskId,
        int? total,
        int? current,
        string? statusMessage,
        string? phase
    )
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

    /// <summary>
    ///     Gets the remote repair state for a specific task ID.
    /// </summary>
    public RemoteRepairState? GetRemoteRepairState(int taskId)
    {
        using (_lock.EnterScope())
        {
            return remoteRepairStates.TryGetValue(taskId, out RemoteRepairState? state)
                ? state
                : null;
        }
    }

    public async Task TaskCompleted(int taskId)
    {
        DateTime time = DateTime.UtcNow;

        using (_lock.EnterScope())
        {
            runningTasks.Remove(taskId);
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? dbTask = await dbContext.PersistedTasks.FirstOrDefaultAsync(t =>
            t.Id == taskId
        );
        if (dbTask == null)
        {
            return;
        }

        bool repairSuccess = true;
        if (dbTask.Data is RepairUpscaleTask repairTask)
        {
            repairSuccess = await HandleRepairTaskCompletion(
                repairTask,
                dbTask,
                scope.ServiceProvider
            );
        }

        if (repairSuccess)
        {
            dbTask.ProcessedAt = time;
            dbTask.Status = PersistedTaskStatus.Completed;
            _ = StatusChanged?.Invoke(dbTask);
            await dbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    ///     Handles the completion of a repair task by merging the upscaled missing pages back into the original CBZ.
    ///     Returns true if the repair was completed successfully, false otherwise.
    /// </summary>
    private async Task<bool> HandleRepairTaskCompletion(
        RepairUpscaleTask repairTask,
        PersistedTask persistedTask,
        IServiceProvider services
    )
    {
        var logger = services.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var repairService = services.GetRequiredService<IRepairService>();
        var metadataHandling = services.GetRequiredService<IMetadataHandlingService>();

        try
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(c => c.Manga)
                    .ThenInclude(m => m.Library)
                .FirstOrDefaultAsync(c => c.Id == repairTask.ChapterId);

            if (chapter == null)
            {
                logger.LogError(
                    "Chapter {chapterId} not found for repair task {taskId}",
                    repairTask.ChapterId,
                    persistedTask.Id
                );
                return false;
            }

            if (chapter.UpscaledFullPath == null)
            {
                logger.LogError(
                    "Upscaled path not set for chapter {chapterId}",
                    repairTask.ChapterId
                );
                return false;
            }

            // Get the stored repair state
            RemoteRepairState? repairState;
            using (_lock.EnterScope())
            {
                if (!remoteRepairStates.TryGetValue(persistedTask.Id, out repairState))
                {
                    logger.LogError(
                        "No repair state found for repair task {taskId}",
                        persistedTask.Id
                    );
                    return false;
                }
            }

            // Check if the repair is still needed (files might have changed)
            string originalPath = chapter.NotUpscaledFullPath;
            string upscaledPath = chapter.UpscaledFullPath;

            PageDifferenceResult differences = await metadataHandling.AnalyzePageDifferencesAsync(
                originalPath,
                upscaledPath
            );
            if (differences.AreEqual)
            {
                logger.LogInformation(
                    "Chapter \"{chapterFileName}\" of {seriesTitle} no longer needs repair, marking as completed",
                    chapter.FileName,
                    chapter.Manga.PrimaryTitle
                );
                // Clean up repair state since repair is no longer needed
                CleanupRepairFiles(persistedTask.Id, logger);
                return true;
            }

            // Verify the upscaled missing pages file exists
            if (
                string.IsNullOrEmpty(repairState.UpscaledMissingPagesCbzPath)
                || !File.Exists(repairState.UpscaledMissingPagesCbzPath)
            )
            {
                logger.LogError(
                    "Upscaled missing pages CBZ not found for repair task {taskId}: {path}",
                    persistedTask.Id,
                    repairState.UpscaledMissingPagesCbzPath
                );
                return false;
            }

            if (repairState.RepairContext is null)
            {
                logger.LogError(
                    "No RepairContext found for repair task {taskId}",
                    persistedTask.Id
                );
                return false;
            }

            RepairContext? repairContext = repairState.RepairContext;

            try
            {
                persistedTask.Data.Progress.StatusMessage = "Merging repaired pages";
                _ = StatusChanged?.Invoke(persistedTask);

                repairService.MergeRepairResults(repairContext, upscaledPath, logger);

                logger.LogInformation(
                    "Successfully completed remote repair of chapter \"{chapterFileName}\" of {seriesTitle}",
                    chapter.FileName,
                    chapter.Manga.PrimaryTitle
                );

                var chapterChangedNotifier = services.GetRequiredService<IChapterChangedNotifier>();
                _ = chapterChangedNotifier.Notify(chapter, true);

                // Apply any title changes that happened in the meantime
                var metadataChanger = services.GetRequiredService<IMangaMetadataChanger>();
                await metadataChanger.ApplyMangaTitleToUpscaledAsync(
                    chapter,
                    chapter.Manga.PrimaryTitle,
                    upscaledPath
                );

                return true;
            }
            finally
            {
                CleanupRepairFiles(persistedTask.Id, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete repair task {taskId}", persistedTask.Id);
            CleanupRepairFiles(persistedTask.Id, logger);
            return false;
        }
    }

    /// <summary>
    ///     Cleans up temporary files created during repair processing.
    /// </summary>
    private void CleanupRepairFiles(int taskId, ILogger logger)
    {
        try
        {
            RemoteRepairState? repairState;
            using (_lock.EnterScope())
            {
                if (!remoteRepairStates.TryGetValue(taskId, out repairState))
                {
                    return; // No repair state found, nothing to clean up
                }

                // Dispose prepared context if present
                try
                {
                    repairState.RepairContext?.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "RepairContext dispose threw, continuing cleanup for task {taskId}",
                        taskId
                    );
                }

                remoteRepairStates.Remove(taskId);
            }

            if (
                !string.IsNullOrEmpty(repairState.PreparedMissingPagesCbzPath)
                && File.Exists(repairState.PreparedMissingPagesCbzPath)
            )
            {
                File.Delete(repairState.PreparedMissingPagesCbzPath);
                logger.LogDebug(
                    "Cleaned up prepared missing pages CBZ: {path}",
                    repairState.PreparedMissingPagesCbzPath
                );
            }

            if (
                !string.IsNullOrEmpty(repairState.UpscaledMissingPagesCbzPath)
                && File.Exists(repairState.UpscaledMissingPagesCbzPath)
            )
            {
                File.Delete(repairState.UpscaledMissingPagesCbzPath);
                logger.LogDebug(
                    "Cleaned up upscaled missing pages CBZ: {path}",
                    repairState.UpscaledMissingPagesCbzPath
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up repair files for task {taskId}", taskId);
        }
    }

    public async Task TaskFailed(int taskId, string? errorMessage)
    {
        using (IServiceScope scope = scopeFactory.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<
                ILogger<DistributedUpscaleTaskProcessor>
            >();
            if (!string.IsNullOrEmpty(errorMessage))
            {
                logger.LogWarning(
                    "Task {taskId} failed on remote worker: {errorMessage}",
                    taskId,
                    errorMessage
                );
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
        PersistedTask? localTask = await dbContext.PersistedTasks.FirstOrDefaultAsync(t =>
            t.Id == taskId
        );
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
    /// Prepares a RepairUpscaleTask for remote processing by analyzing differences and preparing missing pages CBZ.
    /// Returns true if the task was successfully prepared and should be delegated to remote workers.
    /// </summary>
    private async Task<bool> PrepareRepairTaskForRemote(
        RepairUpscaleTask repairTask,
        PersistedTask persistedTask,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        var logger = services.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Load chapter and upscaler profile
            Chapter? chapter = await dbContext
                .Chapters.Include(c => c.Manga)
                    .ThenInclude(m => m.Library)
                        .ThenInclude(l => l.UpscalerProfile)
                .Include(c => c.UpscalerProfile)
                .FirstOrDefaultAsync(c => c.Id == repairTask.ChapterId, cancellationToken);

            UpscalerProfile? upscalerProfile =
                chapter?.UpscalerProfile
                ?? await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
                    c => c.Id == repairTask.UpscalerProfileId,
                    cancellationToken
                );

            if (chapter == null || upscalerProfile == null)
            {
                logger.LogError(
                    "Chapter ({chapterPath}) or upscaler profile ({profileName}, id: {profileId}) not found",
                    chapter?.RelativePath ?? "Not found",
                    upscalerProfile?.Name ?? "Not found",
                    repairTask.UpscalerProfileId
                );
                return false;
            }

            if (chapter.UpscaledFullPath == null)
            {
                logger.LogError(
                    "Upscaled library path of library {libraryName} ({libraryId}) not set",
                    chapter.Manga?.Library?.Name ?? "Unknown",
                    chapter.Manga?.Library?.Id
                );
                return false;
            }

            string upscaleTargetPath = chapter.UpscaledFullPath;
            string currentStoragePath = chapter.NotUpscaledFullPath;

            logger.LogInformation(
                "Preparing remote repair of chapter \"{chapterFileName}\" of {seriesTitle}",
                chapter.FileName,
                chapter.Manga.PrimaryTitle
            );

            var metadataHandling = services.GetRequiredService<IMetadataHandlingService>();
            PageDifferenceResult differences = await metadataHandling.AnalyzePageDifferencesAsync(
                currentStoragePath,
                upscaleTargetPath
            );

            if (differences.AreEqual)
            {
                logger.LogInformation(
                    "Chapter \"{chapterFileName}\" of {seriesTitle} no longer needs repair",
                    chapter.FileName,
                    chapter.Manga.PrimaryTitle
                );

                // Mark as completed and don't delegate to remote workers
                persistedTask.Status = PersistedTaskStatus.Completed;
                persistedTask.ProcessedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _ = StatusChanged?.Invoke(persistedTask);
                return false;
            }

            if (!differences.CanRepair)
            {
                logger.LogWarning(
                    "Chapter \"{chapterFileName}\" of {seriesTitle} cannot be repaired - will fall back to full re-upscale",
                    chapter.FileName,
                    chapter.Manga.PrimaryTitle
                );

                // Fall back to full upscale by creating a regular UpscaleTask and enqueuing it
                var fallbackTask = new UpscaleTask(chapter, upscalerProfile);
                await taskQueue.EnqueueAsync(fallbackTask);

                // Mark original repair task as completed since we've handled the fallback
                persistedTask.Status = PersistedTaskStatus.Completed;
                persistedTask.ProcessedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _ = StatusChanged?.Invoke(persistedTask);
                return false;
            }

            // Prepare the repair context for remote processing
            var repairService = services.GetRequiredService<IRepairService>();
            RepairContext repairContext = repairService.PrepareRepairContext(
                differences,
                currentStoragePath,
                upscaleTargetPath,
                logger
            );

            if (differences.MissingPages.Count > 0)
            {
                // Create and store remote repair state
                var repairState = new RemoteRepairState
                {
                    PreparedMissingPagesCbzPath = repairContext.MissingPagesCbz,
                    UpscaledMissingPagesCbzPath = repairContext.UpscaledMissingCbz,
                    RepairContext = repairContext,
                };

                using (_lock.EnterScope())
                {
                    remoteRepairStates[persistedTask.Id] = repairState;
                }

                persistedTask.Data.Progress.StatusMessage = "Prepared for remote upscaling";
                persistedTask.Data.Progress.Total = differences.MissingPages.Count;
                persistedTask.Data.Progress.Current = 0;
                persistedTask.Data.Progress.ProgressUnit = "pages";
                _ = StatusChanged?.Invoke(persistedTask);

                logger.LogInformation(
                    "Prepared {missingCount} missing pages for remote upscaling",
                    differences.MissingPages.Count
                );

                // Task is ready to be delegated to remote workers
                return true;
            }
            else
            {
                // No missing pages, just remove extra pages and complete immediately
                repairService.MergeRepairResults(repairContext, upscaleTargetPath, logger);
                repairContext.Dispose();

                persistedTask.Status = PersistedTaskStatus.Completed;
                persistedTask.ProcessedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _ = StatusChanged?.Invoke(persistedTask);

                logger.LogInformation(
                    "Completed repair with no missing pages (only removed {extraCount} extra pages)",
                    differences.ExtraPages.Count
                );
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to prepare repair task {TaskId} for remote processing",
                persistedTask.Id
            );
            return false;
        }
    }

    /// <summary>
    ///     State information for managing remote repair operations.
    ///     Contains file paths and context data needed for the remote repair workflow.
    /// </summary>
    public class RemoteRepairState
    {
        /// <summary>
        ///     Path to the prepared CBZ file containing missing pages for remote upscaling.
        /// </summary>
        public string PreparedMissingPagesCbzPath { get; set; } = string.Empty;

        /// <summary>
        ///     Path where the upscaled missing pages CBZ will be stored after remote processing.
        /// </summary>
        public string UpscaledMissingPagesCbzPath { get; set; } = string.Empty;

        /// <summary>
        ///     RepairContext instance for reconstructing the repair process during remote processing.
        ///     This context is created during preparation and disposed during cleanup.
        /// </summary>
        public RepairContext? RepairContext { get; set; }
    }
}
