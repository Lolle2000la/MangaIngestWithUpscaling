using System.Threading.Channels;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class DistributedUpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<DistributedUpscaleTaskProcessor> logger,
    ITaskPersistenceService taskPersistenceService
) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly ChannelReader<object> _reader = taskQueue.UpscaleReader;

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
        PersistedTask? currentTask;
        using (_lock.EnterScope())
        {
            if (
                !runningTasks.TryGetValue(checkAgainst.Id, out currentTask)
                || currentTask.Id != checkAgainst.Id
            )
            {
                return;
            }
        }

        int affected;
        try
        {
            affected = await taskPersistenceService.CancelTaskAsync(checkAgainst.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel task {TaskId}.", checkAgainst.Id);
            return;
        }

        PersistedTask? canceled = null;
        using (_lock.EnterScope())
        {
            // Re-check under the lock: the task may have completed/failed while the guarded cancel
            // was in flight, in which case it is already removed and nothing was canceled. Only
            // mirror a Canceled state when the guarded write actually applied.
            if (
                runningTasks.TryGetValue(checkAgainst.Id, out var tracked)
                && ReferenceEquals(tracked, currentTask)
            )
            {
                if (affected > 0)
                {
                    tracked.Status = PersistedTaskStatus.Canceled;
                    canceled = tracked;
                }

                runningTasks.Remove(checkAgainst.Id);
            }
        }

        // The guarded cancel is the write that took effect, so the UI must be told about Canceled
        // even when TaskCompleted raced us and already dropped the task from runningTasks (its own
        // guarded completion then affects no row and emits nothing). The removed reference is gone
        // from tracking, so emit a shallow snapshot built from the task captured up front. When
        // affected == 0 a terminal state already won, so nothing is emitted and a contradictory
        // status can never be raised.
        if (affected > 0 && canceled == null)
        {
            canceled = new PersistedTask
            {
                Id = currentTask.Id,
                Data = currentTask.Data,
                Status = PersistedTaskStatus.Canceled,
                CreatedAt = currentTask.CreatedAt,
                RetryCount = currentTask.RetryCount,
                ProcessedAt = currentTask.ProcessedAt,
                Order = currentTask.Order,
                LastKeepAlive = currentTask.LastKeepAlive,
            };
        }

        CleanupRepairFiles(checkAgainst.Id, logger);

        // Raise the event after releasing _lock: a subscriber that re-enters the processor (e.g. to
        // query running state) must never be able to deadlock the processor by calling back in.
        if (canceled != null)
        {
            _ = StatusChanged?.Invoke(canceled);
        }
    }

    private Task OnTaskRemoved(PersistedTask task)
    {
        // Removal is authoritative and the row is already gone, so this must not write a status.
        // It also runs while TaskQueue._enqueueSemaphore is held (for the cleanup-driven removal),
        // so it stays synchronous and fast instead of awaiting a database write.
        ForgetTask(task.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stops tracking a task whose row has already been removed so remote workers stop receiving
    ///     keep-alives for it. Unlike <see cref="CancelCurrent" /> this never persists a status: the
    ///     row no longer exists, so a write would be both pointless and a concurrency hazard.
    /// </summary>
    public void ForgetTask(int taskId)
    {
        using (_lock.EnterScope())
        {
            if (!runningTasks.Remove(taskId))
            {
                return;
            }
        }

        CleanupRepairFiles(taskId, logger);
    }

    /// <summary>
    ///     Persists a terminal <see cref="PersistedTaskStatus.Completed" /> state and drops the task
    ///     from the running set. Used by the skip/terminal branches that previously only mutated the
    ///     in-memory task, which left the database row stuck in <see cref="PersistedTaskStatus.Processing" />.
    /// </summary>
    private async Task PersistCompletedAsync(
        PersistedTask task,
        CancellationToken cancellationToken
    )
    {
        RemoveFromRunningTasks(task.Id);

        int affected;
        try
        {
            affected = await taskPersistenceService.CompleteTaskAsync(task.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist completion of task {TaskId}.", task.Id);
            return;
        }

        // A guarded terminal write affects no row when the task is already terminal. The database
        // is the source of truth then, so do not overwrite the in-memory/UI state.
        if (affected == 0)
        {
            return;
        }

        task.Status = PersistedTaskStatus.Completed;
        task.ProcessedAt = DateTime.UtcNow;
        _ = StatusChanged?.Invoke(task);
    }

    /// <summary>
    ///     Persists a terminal <see cref="PersistedTaskStatus.Failed" /> state and drops the task from
    ///     the running set. See <see cref="PersistCompletedAsync" /> for why this is needed.
    /// </summary>
    private async Task PersistFailedAsync(PersistedTask task, CancellationToken cancellationToken)
    {
        RemoveFromRunningTasks(task.Id);

        int affected;
        try
        {
            affected = await taskPersistenceService.FailTaskAsync(task.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist failure of task {TaskId}.", task.Id);
            return;
        }

        if (affected == 0)
        {
            return;
        }

        task.Status = PersistedTaskStatus.Failed;
        task.ProcessedAt = DateTime.UtcNow;
        task.RetryCount++;
        _ = StatusChanged?.Invoke(task);
    }

    private void RemoveFromRunningTasks(int taskId)
    {
        using (_lock.EnterScope())
        {
            runningTasks.Remove(taskId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;

        // Removal is an authoritative stop signal: if the queue removes a task that is currently
        // being processed remotely, stop tracking it and cancel it in the database.
        taskQueue.TaskRemoved += OnTaskRemoved;

        _ = Task.Run(
            async () =>
            {
                using var cleanDeadTasksTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                try
                {
                    while (
                        !stoppingToken.IsCancellationRequested
                        && await cleanDeadTasksTimer.WaitForNextTickAsync(stoppingToken)
                    )
                    {
                        try
                        {
                            await ReapDeadTasksAsync(stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            // A single transient failure (e.g. DbUpdateConcurrencyException from a
                            // concurrently deleted row) must not kill the reaper; retry next tick.
                            logger.LogError(
                                ex,
                                "Failed to reap dead tasks; the reaper will retry on the next tick."
                            );
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown: WaitForNextTickAsync throws when stoppingToken is canceled.
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Dead task reaper terminated unexpectedly.");
                }
            },
            stoppingToken
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            (TaskCompletionSource<PersistedTask> tcs, CancellationToken cancelToken) =
                await _taskRequests.Reader.ReadAsync(stoppingToken);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
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

            // Tracks a task this processor claimed but has not yet handed off (to the requesting
            // worker, to the local processor, or to a terminal state). If the worker cancels its
            // request, or an unexpected error happens before handoff, the task must be requeued
            // instead of being left stranded in Processing: it is no longer in the in-memory
            // upscale set, was never added to runningTasks, and ReplayPendingOrFailed ignores it.
            PersistedTask? claimedTask = null;
            try
            {
                bool completed = false;
                while (!completed && !stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await _reader.ReadAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                        when (linkedCts.IsCancellationRequested
                            && !stoppingToken.IsCancellationRequested
                        )
                    {
                        // The specific request timed out or was cancelled, but the processor should keep running.
                        break;
                    }

                    PersistedTask? task = taskQueue.DequeueUpscale();

                    if (task == null)
                    {
                        continue;
                    }

                    // Track the task before the claim. ClaimTaskAsync can throw after the row was
                    // already committed to Processing, so the outer catch must recover the claim
                    // itself; assigning only after a successful claim left such a row stranded.
                    claimedTask = task;

                    using (IServiceScope scope = scopeFactory.CreateScope())
                    {
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();

                        if (await taskPersistenceService.ClaimTaskAsync(task.Id, linkedCts.Token))
                        {
                            task.Status = PersistedTaskStatus.Processing;
                        }
                        else
                        {
                            logger.LogInformation(
                                "Task {taskId} could not be claimed (already processed or concurrency conflict).",
                                task.Id
                            );
                            // Not ours to recover: it is already claimed elsewhere or terminal.
                            claimedTask = null;
                            continue;
                        }
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
                                // PrepareRepairTaskForRemote already requeued the task or persisted
                                // a terminal state, so it is no longer ours to recover.
                                claimedTask = null;
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
                            // Handed off to the local processor; it owns the task from here.
                            claimedTask = null;
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
                        // Handed off to the local processor; it owns the task from here.
                        claimedTask = null;
                        continue;
                    }

                    if (task.Data is ApplySplitsTask applySplitsTask)
                    {
                        // Check if the chapter exists
                        using IServiceScope scope = scopeFactory.CreateScope();
                        var logger = scope.ServiceProvider.GetRequiredService<
                            ILogger<DistributedUpscaleTaskProcessor>
                        >();
                        var dbContext =
                            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        Chapter? chapter = await dbContext
                            .Chapters.Include(t => t.Manga)
                                .ThenInclude(t => t.Library)
                            .FirstOrDefaultAsync(
                                c => c.Id == applySplitsTask.ChapterId,
                                linkedCts.Token
                            );

                        if (chapter == null || !File.Exists(chapter.NotUpscaledFullPath))
                        {
                            // Chapter no longer exists, mark task as failed (and persist it so a
                            // restart does not replay a task that is already terminal). The terminal
                            // write uses the service token so a worker disconnect cannot cancel it.
                            await PersistFailedAsync(task, serviceStoppingToken);
                            claimedTask = null;

                            logger.LogWarning(
                                "Skipping ApplySplitsTask {taskId} because chapter file is missing.",
                                task.Id
                            );
                            continue;
                        }
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
                            // Chapter no longer exists, mark task as failed (persisted, so a
                            // restart does not replay a task that is already terminal). The terminal
                            // write uses the service token so a worker disconnect cannot cancel it.
                            await PersistFailedAsync(task, serviceStoppingToken);
                            claimedTask = null;

                            logger.LogWarning(
                                "Skipping task {taskId} because chapter file is missing.",
                                task.Id
                            );
                            continue;
                        }

                        // check if it is already upscaled
                        if (chapter.IsUpscaled)
                        {
                            await PersistCompletedAsync(task, serviceStoppingToken);
                            claimedTask = null;
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

                            // Check if splits were just applied
                            var splitState =
                                await dbContext.ChapterSplitProcessingStates.FirstOrDefaultAsync(
                                    s => s.ChapterId == upscaleData.ChapterId,
                                    linkedCts.Token
                                );

                            bool splitsJustApplied =
                                splitState != null
                                && splitState.Status == SplitProcessingStatus.Applied
                                && splitState.LastAppliedDetectorVersion
                                    == SplitDetectionService.CURRENT_DETECTOR_VERSION;

                            if (
                                splitsJustApplied
                                || await metadataHandling.PagesEqualAsync(
                                    chapter.NotUpscaledFullPath,
                                    chapter.UpscaledFullPath
                                )
                            )
                            {
                                // Double check pages equality if we relied on split state, just to be safe
                                if (
                                    splitsJustApplied
                                    && !await metadataHandling.PagesEqualAsync(
                                        chapter.NotUpscaledFullPath,
                                        chapter.UpscaledFullPath
                                    )
                                )
                                {
                                    // If pages don't match despite splits being applied, schedule a repair
                                    UpscalerProfile? profile =
                                        (
                                            chapter.UpscalerProfile?.Id
                                            == upscaleData.UpscalerProfileId
                                        )
                                            ? chapter.UpscalerProfile
                                            : await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
                                                p => p.Id == upscaleData.UpscalerProfileId,
                                                linkedCts.Token
                                            );

                                    if (profile != null)
                                    {
                                        logger.LogInformation(
                                            "Pages do not match for chapter {chapterId} despite splits being applied. Scheduling RepairUpscaleTask.",
                                            chapter.Id
                                        );
                                        var newRepairTask = new RepairUpscaleTask(chapter, profile);
                                        await taskQueue.EnqueueAsync(newRepairTask);

                                        await PersistCompletedAsync(task, serviceStoppingToken);
                                        claimedTask = null;
                                        continue;
                                    }
                                }
                                else
                                {
                                    chapter.IsUpscaled = true;
                                    await dbContext.SaveChangesAsync(linkedCts.Token);
                                    await PersistCompletedAsync(task, serviceStoppingToken);
                                    claimedTask = null;
                                    logger.LogInformation(
                                        "Skipping task {taskId} because target file already exists and is equal (SplitsApplied: {splitsApplied}).",
                                        task.Id,
                                        splitsJustApplied
                                    );
                                    continue;
                                }
                            }
                        }
                    }

                    if (!tcs.TrySetResult(task))
                    {
                        // Requester couldn't accept the task (likely cancelled). Use the same
                        // bounded recovery as other worker disconnects so a repeated handoff
                        // failure respects the task's RetryFor budget instead of requeueing
                        // unboundedly.
                        await RequeueClaimedTaskAsync(task);
                    }

                    claimedTask = null;
                    completed = true; // Task has been successfully passed or re-enqueued.
                }
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(linkedCts.Token);

                // A claimed task that never reached a handoff is stranded in Processing unless it
                // is requeued here: it is no longer in the in-memory set and the reaper only sees
                // runningTasks.
                if (claimedTask != null)
                {
                    await RequeueClaimedTaskAsync(claimedTask);
                }

                // A request/worker token cancellation (worker disconnect or request timeout) must
                // not terminate ExecuteAsync. Only stop when the service itself is stopping.
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                logger.LogDebug(
                    "Task request was cancelled by the requesting worker; continuing to serve remote workers."
                );
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);

                // An unexpected error after the claim but before handoff must not strand the task.
                if (claimedTask != null)
                {
                    await RequeueClaimedTaskAsync(claimedTask);
                }
            }
        }
    }

    /// <summary>
    ///     Requeues remote tasks that have stopped sending keep-alives. Each dead task is handled
    ///     independently and goes through the same bounded recovery as a worker disconnect: while
    ///     under its <c>RetryFor</c> budget the row is returned to Pending with an incremented retry
    ///     count and re-added to the in-memory queue; at/over budget (or when <c>RetryFor</c> is zero)
    ///     it becomes a terminal failure instead of being reaped forever. A failure affecting one task
    ///     re-adds it to <c>runningTasks</c> for the next tick instead of dropping it or preventing
    ///     the remaining tasks from being requeued.
    /// </summary>
    internal async Task ReapDeadTasksAsync(CancellationToken cancellationToken)
    {
        List<PersistedTask> deadTasks;
        using (_lock.EnterScope())
        {
            deadTasks = runningTasks
                .Values.Where(t =>
                    t.Status == PersistedTaskStatus.Processing
                    && t.LastKeepAlive.AddMinutes(1) < DateTime.UtcNow
                )
                .ToList();

            // Stop tracking each dead task up front. A task whose requeue below fails is re-added, so
            // one per-task failure cannot drop the rest or strand a row until restart.
            foreach (PersistedTask task in deadTasks)
            {
                runningTasks.Remove(task.Id);
            }
        }

        if (deadTasks.Count == 0)
        {
            return;
        }

        logger.LogInformation("Re-enqueuing {count} dead tasks.", deadTasks.Count);

        foreach (PersistedTask task in deadTasks)
        {
            try
            {
                // A dead remote task may have left a prepared repair context and its temp files
                // behind. Clean them before requeueing so the next preparation cannot leak them
                // (CleanupRepairFiles respects any in-flight completion's usage lease).
                CleanupRepairFiles(task.Id, logger);

                // Budgeted recovery consumes exactly one attempt: under budget the row returns to
                // Pending and is re-enqueued; at/over budget it is persisted as Failed. A terminal
                // row (already completed/canceled while the task was dead) is left alone.
                await ApplyBudgetedRecoveryAsync(task, cancellationToken);
            }
            catch (Exception ex)
            {
                // Keep the task tracked so the next tick retries it. This must not prevent the
                // remaining dead tasks from being requeued.
                using (_lock.EnterScope())
                {
                    runningTasks.TryAdd(task.Id, task);
                }

                logger.LogError(
                    ex,
                    "Failed to requeue dead task {taskId}; the reaper will retry it on the next tick.",
                    task.Id
                );
            }
        }
    }

    /// <summary>
    /// Returns the head of the upscale queue without claiming it, so a worker can inspect the
    /// task (e.g. its transfer size) before deciding whether to claim it.
    /// </summary>
    public PersistedTask? PeekTask()
    {
        return taskQueue.PeekUpscale();
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
        PersistedTask? currentTask;
        using (_lock.EnterScope())
        {
            if (!runningTasks.TryGetValue(taskId, out currentTask))
            {
                return false;
            }

            currentTask.LastKeepAlive = DateTime.UtcNow;
        }

        // Notify listeners (e.g., TaskRegistry/UI) that the task heartbeat was updated, after the
        // lock is released so subscribers can safely re-enter the processor.
        _ = StatusChanged?.Invoke(currentTask);
        return true;
    }

    /// <summary>
    ///     Checks if a task is currently running on a remote processor.
    /// </summary>
    public bool IsRunningRemotely(int taskId)
    {
        using (_lock.EnterScope())
        {
            return runningTasks.ContainsKey(taskId);
        }
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
        PersistedTask? task;
        using (_lock.EnterScope())
        {
            if (!runningTasks.TryGetValue(taskId, out task))
            {
                return;
            }

            ProgressInfo p = task.Data.Progress;
            if (total.HasValue)
            {
                p.Total = total.Value;
            }

            if (current.HasValue)
            {
                p.Current = current.Value;
            }

            // Treat progress updates as heartbeats as well, to keep liveness fresh
            task.LastKeepAlive = DateTime.UtcNow;
        }

        _ = StatusChanged?.Invoke(task);
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
            int affected = await taskPersistenceService.CompleteTaskAsync(taskId);
            if (affected == 0)
            {
                // The row was already terminal (e.g. canceled) when this late completion arrived.
                // The database is the source of truth, so do not emit a contradictory status.
                return;
            }

            dbTask.ProcessedAt = time;
            dbTask.Status = PersistedTaskStatus.Completed;
            _ = StatusChanged?.Invoke(dbTask);
        }
        else
        {
            // HandleRepairTaskCompletion returns false when the merge could not be applied (missing
            // chapter, missing repair state or an unexpected error) and has already logged it. Drop
            // any remaining repair state and persist a terminal Failed state so the row is not left
            // Processing until the next startup reset.
            var logger = scope.ServiceProvider.GetRequiredService<
                ILogger<DistributedUpscaleTaskProcessor>
            >();
            CleanupRepairFiles(taskId, logger);
            await PersistFailedAsync(dbTask, serviceStoppingToken);
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

        // Get the stored repair state and take a usage lease before any await. Cleanup
        // (cancel/forget) removes the state and requests disposal, but disposal is deferred until
        // this completion releases the lease, so the RepairContext and its work directory cannot be
        // torn down underneath the in-progress completion.
        RemoteRepairState? repairState;
        using (_lock.EnterScope())
        {
            if (!remoteRepairStates.TryGetValue(persistedTask.Id, out repairState))
            {
                logger.LogError("No repair state found for repair task {taskId}", persistedTask.Id);
                return false;
            }
        }

        if (!repairState.TryBeginUse())
        {
            logger.LogDebug(
                "Repair state for task {taskId} is already being cleaned up; skipping completion.",
                persistedTask.Id
            );
            return false;
        }

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
                // persistedTask.Data.Progress.StatusMessage = "Merging repaired pages";
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
        finally
        {
            repairState.EndUse(logger);
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

                remoteRepairStates.Remove(taskId);
            }

            // Dispose the repair context and delete its files after releasing _lock: RepairContext.Dispose
            // recursively deletes a directory, and holding _lock across that IO would block keep-alives,
            // progress updates, cancellation and IsRunningRemotely for the duration. If a completion
            // still holds a usage lease, the state defers cleanup until that lease is released.
            repairState.RequestCleanup(logger);
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

        PersistedTask? failedTask = null;
        using (_lock.EnterScope())
        {
            if (runningTasks.TryGetValue(taskId, out PersistedTask? task))
            {
                runningTasks.Remove(taskId);
                failedTask = task;
            }
        }

        int affected = await taskPersistenceService.FailTaskAsync(taskId);

        // Only mirror the failure in memory when the guarded transition actually happened; a late
        // failure after a cancel/completion must not overwrite the terminal state.
        if (failedTask != null && affected > 0)
        {
            failedTask.Status = PersistedTaskStatus.Failed;
            failedTask.RetryCount++;
            _ = StatusChanged?.Invoke(failedTask);
        }

        // Fetch the updated task from the database to notify listeners (e.g., UI).
        // This ensures the UI reflects the persisted status and updated retry count,
        // even if the task wasn't tracked in the 'runningTasks' dictionary.
        using IServiceScope dbScope = scopeFactory.CreateScope();
        var dbContext = dbScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? localTask = await dbContext
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId);

        if (localTask != null)
        {
            _ = StatusChanged?.Invoke(localTask);
        }
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
            // Drop any repair state left over from a previous attempt before preparing a new one:
            // a requeued dead task can carry a stale context whose prepared CBZs would otherwise
            // leak (or be overwritten without disposal below). CleanupRepairFiles respects an
            // in-flight completion's usage lease, so it cannot dispose a context still in use.
            CleanupRepairFiles(persistedTask.Id, logger);

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
                await PersistFailedAsync(persistedTask, serviceStoppingToken);
                return false;
            }

            if (chapter.UpscaledFullPath == null)
            {
                logger.LogError(
                    "Upscaled library path of library {libraryName} ({libraryId}) not set",
                    chapter.Manga?.Library?.Name ?? "Unknown",
                    chapter.Manga?.Library?.Id
                );
                await PersistFailedAsync(persistedTask, serviceStoppingToken);
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

                // Mark as completed and don't delegate to remote workers. Terminal writes use the
                // service token so a worker disconnect cannot cancel them.
                await PersistCompletedAsync(persistedTask, serviceStoppingToken);
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
                await PersistCompletedAsync(persistedTask, serviceStoppingToken);
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

                // persistedTask.Data.Progress.StatusMessage = "Prepared for remote upscaling";
                persistedTask.Data.Progress.Total = differences.MissingPages.Count;
                persistedTask.Data.Progress.Current = 0;
                // persistedTask.Data.Progress.ProgressUnit = "pages";
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

                await PersistCompletedAsync(persistedTask, serviceStoppingToken);

                logger.LogInformation(
                    "Completed repair with no missing pages (only removed {extraCount} extra pages)",
                    differences.ExtraPages.Count
                );
                return false;
            }
        }
        catch (OperationCanceledException) when (!serviceStoppingToken.IsCancellationRequested)
        {
            // The requesting worker disconnected (or its request was cancelled) while the task was
            // being prepared. This is not a task failure: do not persist a terminal state and do not
            // bump the retry count. Requeue it so it stays replayable instead of being abandoned.
            logger.LogDebug(
                "Preparation of repair task {TaskId} was cancelled by the requesting worker; requeuing.",
                persistedTask.Id
            );
            CleanupRepairFiles(persistedTask.Id, logger);
            await RequeueClaimedTaskAsync(persistedTask);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to prepare repair task {TaskId} for remote processing",
                persistedTask.Id
            );
            CleanupRepairFiles(persistedTask.Id, logger);
            await PersistFailedAsync(persistedTask, serviceStoppingToken);
            return false;
        }
    }

    /// <summary>
    ///     Puts a task that was claimed (or was being claimed) by this processor but never
    ///     successfully handed off back into the queue as Pending. This covers a worker disconnect
    ///     during remote-repair preparation as well as a cancellation or unexpected error during or
    ///     after a plain claim.
    ///     <para>
    ///         A request/worker-cancellation disconnect consumes the task's bounded retry budget:
    ///         while <c>RetryCount + 1 &lt; RetryFor</c> the row is returned to Pending with an
    ///         incremented retry count and re-enqueued immediately; once the budget is reached (or
    ///         when <c>RetryFor</c> is zero) the disconnect is persisted as a terminal failure so it
    ///         cannot retry forever. Service shutdown does not consume the budget: it falls back to a
    ///         plain guarded reconcile so startup recovery owns the task.
    ///     </para>
    ///     The database writes are guarded so a terminal row is never resurrected; when the row is no
    ///     longer recoverable the task is simply dropped. Failures are logged and swallowed: the row
    ///     would then be left for startup recovery, so a database hiccup here must not fault the
    ///     processor.
    /// </summary>
    private async Task RequeueClaimedTaskAsync(PersistedTask persistedTask)
    {
        if (serviceStoppingToken.IsCancellationRequested)
        {
            // Service shutdown is not a task failure and must not consume the retry budget.
            await RequeueWithoutRetryBudgetAsync(persistedTask);
            return;
        }

        try
        {
            await ApplyBudgetedRecoveryAsync(persistedTask, serviceStoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to requeue claimed task {TaskId}; leaving it for startup recovery.",
                persistedTask.Id
            );
        }
    }

    /// <summary>
    ///     Applies the task's bounded retry budget to a single recovery event (a worker disconnect,
    ///     a failed handoff, or a reaped dead task). While <c>RetryCount + 1 &lt; RetryFor</c> the row
    ///     is returned to Pending with an incremented retry count and re-enqueued; at/over budget (or
    ///     when <c>RetryFor</c> is zero) it is persisted as a terminal failure. A row that is already
    ///     terminal is left untouched. Persistence exceptions propagate so each caller can apply its
    ///     own per-task fault isolation.
    /// </summary>
    private async Task<RecoveryOutcome> ApplyBudgetedRecoveryAsync(
        PersistedTask persistedTask,
        CancellationToken cancellationToken
    )
    {
        int retryFor = persistedTask.Data?.RetryFor ?? 0;
        int next = persistedTask.RetryCount + 1;
        if (retryFor <= 0 || next >= retryFor)
        {
            logger.LogWarning(
                "Recovery of task {TaskId} reached its retry budget ({Next}/{RetryFor}); marking it Failed instead of retrying.",
                persistedTask.Id,
                next,
                retryFor
            );
            await PersistFailedAsync(persistedTask, cancellationToken);
            return RecoveryOutcome.Failed;
        }

        int affected = await taskPersistenceService.RequeueStrandedTaskAsync(
            persistedTask.Id,
            cancellationToken,
            incrementRetry: true
        );

        if (affected == 0)
        {
            // The row is already terminal or was removed; do not resurrect it.
            return RecoveryOutcome.Terminal;
        }

        persistedTask.RetryCount = next;
        persistedTask.Status = PersistedTaskStatus.Pending;

        // The guarded update above already persisted Pending, so re-add to the in-memory queue
        // without another database write. The sorted set deduplicates by (Order, Id).
        taskQueue.ReEnqueue(persistedTask);
        _ = StatusChanged?.Invoke(persistedTask);
        return RecoveryOutcome.Requeued;
    }

    private enum RecoveryOutcome
    {
        Requeued,
        Failed,
        Terminal,
    }

    /// <summary>
    ///     The pre-retry-budget reconcile used on service shutdown: returns a still-recoverable row to
    ///     Pending without touching <c>RetryCount</c> and re-adds it in memory. See
    ///     <see cref="RequeueClaimedTaskAsync" /> for the budgeted disconnect path.
    /// </summary>
    private async Task RequeueWithoutRetryBudgetAsync(PersistedTask persistedTask)
    {
        int affected;
        try
        {
            affected = await taskPersistenceService.RequeueStrandedTaskAsync(
                persistedTask.Id,
                serviceStoppingToken
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to requeue claimed task {TaskId}; leaving it for startup recovery.",
                persistedTask.Id
            );
            return;
        }

        if (affected == 0)
        {
            return;
        }

        persistedTask.Status = PersistedTaskStatus.Pending;
        try
        {
            taskQueue.ReEnqueue(persistedTask);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to re-enqueue claimed task {TaskId}; the periodic replay will recover it.",
                persistedTask.Id
            );
        }

        _ = StatusChanged?.Invoke(persistedTask);
    }

    /// <summary>
    ///     State information for managing remote repair operations.
    ///     Contains file paths and context data needed for the remote repair workflow.
    /// </summary>
    public class RemoteRepairState
    {
        private readonly object _usageGate = new();
        private int _activeUsers;
        private bool _cleanupRequested;
        private bool _cleanupCompleted;

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

        /// <summary>
        ///     Marks the state as in use by a completion. Returns <c>false</c> when cleanup has
        ///     already been requested or performed, so the caller must not touch the repair context.
        /// </summary>
        public bool TryBeginUse()
        {
            lock (_usageGate)
            {
                if (_cleanupRequested || _cleanupCompleted)
                {
                    return false;
                }

                _activeUsers++;
                return true;
            }
        }

        /// <summary>
        ///     Releases a usage lease. If cleanup is pending and this was the last user, the repair
        ///     context is disposed and the temporary files are deleted.
        /// </summary>
        public void EndUse(ILogger logger)
        {
            bool runCleanup;
            lock (_usageGate)
            {
                _activeUsers--;
                runCleanup = _cleanupRequested && _activeUsers <= 0 && !_cleanupCompleted;
                if (runCleanup)
                {
                    _cleanupCompleted = true;
                }
            }

            if (runCleanup)
            {
                Cleanup(logger);
            }
        }

        /// <summary>
        ///     Requests cleanup. Cleanup runs immediately unless a completion currently holds a usage
        ///     lease, in which case it is deferred until the lease is released.
        /// </summary>
        public void RequestCleanup(ILogger logger)
        {
            bool runCleanup;
            lock (_usageGate)
            {
                _cleanupRequested = true;
                runCleanup = _activeUsers <= 0 && !_cleanupCompleted;
                if (runCleanup)
                {
                    _cleanupCompleted = true;
                }
            }

            if (runCleanup)
            {
                Cleanup(logger);
            }
        }

        private void Cleanup(ILogger logger)
        {
            try
            {
                RepairContext?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RepairContext dispose threw during remote repair cleanup.");
            }

            DeleteFileIfPresent(PreparedMissingPagesCbzPath, logger);
            DeleteFileIfPresent(UpscaledMissingPagesCbzPath, logger);
        }

        private static void DeleteFileIfPresent(string path, ILogger logger)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
                logger.LogDebug("Cleaned up repair file: {path}", path);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to delete repair file {path}", path);
            }
        }
    }
}
