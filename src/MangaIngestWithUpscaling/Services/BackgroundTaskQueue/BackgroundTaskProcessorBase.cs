using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Shared skeleton for the queue processors. It owns the per-task cancellation surface, the
///     bounded transient-claim retry policy and the common acquire -&gt; process -&gt; guarded
///     terminal-outcome flow. Subclasses supply the channel loop, the task-acquisition step and any
///     task-specific hooks.
/// </summary>
public abstract class BackgroundTaskProcessorBase(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger logger,
    ITaskPersistenceService taskPersistenceService
) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly TimeSpan _progressDebounce = TimeSpan.FromMilliseconds(250);

    // Consecutive transient claim failures per task. A poisoned row is retried promptly but a
    // bounded number of times instead of hot-looping or waiting for the periodic replayer.
    // Successful claims and terminal outcomes remove the entry.
    private readonly ConcurrentDictionary<int, int> _claimAttempts = new();
    private CancellationTokenSource? currentStoppingToken;
    private PersistedTask? currentTask;
    private CancellationToken serviceStoppingToken;

    /// <summary>
    ///     Backoff applied before promptly retrying a transient claim failure. Index 0 is the first
    ///     retry. Once a task has failed more times than there are entries it is left Pending for the
    ///     periodic replayer, bounding retries so a persistent failure cannot hot-loop or starve
    ///     other tasks. Virtual so tests can substitute tiny delays.
    /// </summary>
    protected virtual IReadOnlyList<TimeSpan> ClaimRetryBackoff { get; } =
        new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    /// <summary>
    ///     Message logged when a task's processing throws a non-cancellation exception. Each
    ///     processor keeps its historical wording so the log's meaning and category are preserved.
    /// </summary>
    protected abstract string ProcessingFailedLogMessage { get; }

    protected ILogger Logger => logger;
    protected TaskQueue TaskQueue => taskQueue;
    protected IServiceScopeFactory ScopeFactory => scopeFactory;
    protected CancellationToken ServiceStoppingToken => serviceStoppingToken;

    public event Func<PersistedTask, Task>? StatusChanged;

    /// <summary>
    ///     Wires up the shared cancellation surface and then runs the subclass loop. Sealed because
    ///     the loop is the only part of execution that varies between processors.
    /// </summary>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;

        // Removal is an authoritative stop signal: if the queue removes the task this processor is
        // running, stop it rather than finish work against a deleted row.
        taskQueue.TaskRemoved += OnTaskRemoved;

        await RunAsync(stoppingToken);
    }

    /// <summary>Reads tasks and processes them until the service is stopped.</summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <summary>
    ///     Claims (or otherwise verifies) <paramref name="task" />. Returns <c>false</c> when it must
    ///     not run: it was dropped, its cancel was persisted, or a bounded retry was scheduled.
    /// </summary>
    protected abstract Task<bool> TryAcquireTaskAsync(
        PersistedTask task,
        CancellationToken stoppingToken
    );

    /// <summary>Hook invoked once a task is owned, before its work starts.</summary>
    protected virtual void OnTaskAcquired(PersistedTask task) { }

    /// <summary>
    ///     Hook invoked on every progress change, before the shared debounce gate. The upscale
    ///     processor uses it to drive its prefetch coordinator.
    /// </summary>
    protected virtual void OnProgressChanged(PersistedTask task) { }

    /// <summary>
    /// Cancels the current task if it matches the given task.
    /// The task is necessary to prevent canceling another if the task has already been processed.
    /// Otherwise, consistency issues may arise.
    /// </summary>
    /// <param name="checkAgainst">The task to check against if it is still the current task. Does so by using the Id.</param>
    public void CancelCurrent(PersistedTask checkAgainst)
    {
        using (_lock.EnterScope())
        {
            if (currentTask?.Id == checkAgainst.Id)
            {
                currentStoppingToken?.Cancel();
            }
        }
    }

    private Task OnTaskRemoved(PersistedTask task)
    {
        CancelCurrent(task);
        // A removed task will never be retried, so its pending transient-claim counter must not
        // linger for the lifetime of the process.
        ForgetClaimAttempts(task.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Creates the linked per-task cancellation source and makes <paramref name="task" /> the
    ///     current task, atomically under <c>_lock</c>.
    /// </summary>
    protected CancellationTokenSource BeginCurrentTask(
        PersistedTask task,
        CancellationToken stoppingToken
    )
    {
        using (_lock.EnterScope())
        {
            currentStoppingToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var taskStoppingToken = currentStoppingToken;
            currentTask = task;
            return taskStoppingToken;
        }
    }

    /// <summary>
    ///     Disposes the per-task cancellation source once its task is done so it is not leaked
    ///     across tasks. Disposal is serialized with <see cref="CancelCurrent" /> by <c>_lock</c>.
    /// </summary>
    protected void DisposeCurrentStoppingToken(CancellationTokenSource taskStoppingToken)
    {
        using (_lock.EnterScope())
        {
            if (ReferenceEquals(currentStoppingToken, taskStoppingToken))
            {
                currentStoppingToken.Dispose();
                currentStoppingToken = null;
                currentTask = null;
            }
        }
    }

    /// <summary>
    ///     Removes any recorded transient-claim-attempt counter for <paramref name="taskId" />,
    ///     marking its earlier failures as resolved.
    /// </summary>
    protected void ForgetClaimAttempts(int taskId) => _claimAttempts.TryRemove(taskId, out _);

    /// <summary>
    ///     Claims a Pending task. A claim failure (per-task cancellation from
    ///     <see cref="CancelCurrent" />/removal, or a transient database error) is handled here so it
    ///     cannot fault the processor loop: under
    ///     <c>BackgroundServiceExceptionBehavior.StopHost</c> that would stop the whole application.
    /// </summary>
    /// <returns><c>true</c> when the claim succeeded; otherwise <c>false</c>.</returns>
    protected async Task<bool> ClaimAsync(PersistedTask task, CancellationToken stoppingToken)
    {
        bool claimed;
        try
        {
            claimed = await taskPersistenceService.ClaimTaskAsync(task.Id, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // CancelCurrent (removal or an explicit cancel) cancelled this token. The task was
            // already removed from the in-memory queue, so persist the intended cancel rather than
            // silently dropping it. On service shutdown the task is put back to Pending.
            logger.LogInformation("Claim of task {TaskId} was canceled", task.Id);
            ForgetClaimAttempts(task.Id);
            await ApplyClaimCancellationAsync(task);
            return false;
        }
        catch (Exception ex)
        {
            // A transient claim failure is retried promptly (with a bounded backoff) so it does not
            // have to wait for the 10-minute periodic replayer, but a persistent failure is left
            // Pending after a bounded number of attempts so it cannot hot-loop or starve later
            // tasks. The row is reconciled to Pending first, so the task is never lost.
            logger.LogError(
                ex,
                "Failed to claim task {TaskId}; returning it to Pending for retry",
                task.Id
            );
            await ReturnToPendingForReplayAsync(task);
            await RequeueTransientClaimFailureAsync(task);
            return false;
        }

        if (!claimed)
        {
            // The row is no longer Pending (already claimed elsewhere or terminal), so dropping the
            // in-memory copy is correct: re-adding it would spin on a task this processor cannot own.
            logger.LogInformation(
                "Task {TaskId} could not be claimed (already processed or concurrency conflict)",
                task.Id
            );
            ForgetClaimAttempts(task.Id);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Runs a task that the subclass has acquired: publishes Processing, subscribes to its
    ///     progress and performs the guarded Completed/Canceled/Failed terminal writes.
    /// </summary>
    protected async Task ProcessTaskAsync(PersistedTask task, CancellationToken stoppingToken)
    {
        if (!await TryAcquireTaskAsync(task, stoppingToken))
        {
            return;
        }

        // The claim (or reroute verification) succeeded, so any earlier transient failures are
        // resolved.
        ForgetClaimAttempts(task.Id);

        // Update the in-memory task status
        task.Status = PersistedTaskStatus.Processing;
        StatusChanged?.Invoke(task);

        using var scope = scopeFactory.CreateScope();
        OnTaskAcquired(task);

        try
        {
            // Polymorphic processing based on concrete type, forward debounced progress to UI
            var last = DateTime.UtcNow;
            using var progressSubscription = task.Data.Progress.Changed.Subscribe(_ =>
            {
                OnProgressChanged(task);

                var now = DateTime.UtcNow;
                if (now - last >= _progressDebounce)
                {
                    last = now;
                    StatusChanged?.Invoke(task);
                }
            });

            await task.Data.ProcessAsync(scope.ServiceProvider, stoppingToken);
            StatusChanged?.Invoke(task);

            int completedRows = await taskPersistenceService.CompleteTaskAsync(task.Id);

            // A guarded terminal write affects no row when the task was concurrently canceled or
            // removed. The database is the source of truth then, so do not overwrite the in-memory
            // (and UI) state with a contradictory Completed status.
            if (completedRows == 0)
            {
                return;
            }

            task.Status = PersistedTaskStatus.Completed;
            task.ProcessedAt = DateTime.UtcNow;
            StatusChanged?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {TaskId} was canceled", task.Id);
            bool requeue = serviceStoppingToken.IsCancellationRequested;
            try
            {
                int canceledRows = await taskPersistenceService.CancelTaskAsync(task.Id, requeue);

                // A guarded cancel affects no row when the task already reached a terminal state;
                // do not emit a contradictory Canceled status in that case.
                if (canceledRows == 0)
                {
                    return;
                }

                task.Status = requeue ? PersistedTaskStatus.Pending : PersistedTaskStatus.Canceled;
                StatusChanged?.Invoke(task);
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to update task {TaskId} status", task.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ProcessingFailedLogMessage, task.Id);
            try
            {
                int failedRows = await taskPersistenceService.FailTaskAsync(task.Id);
                if (failedRows == 0)
                {
                    // Already terminal (e.g. concurrently canceled); do not emit a contradictory
                    // Failed status or inflate the in-memory retry count.
                    return;
                }

                task.Status = PersistedTaskStatus.Failed;
                task.RetryCount++;
                StatusChanged?.Invoke(task);
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to update task {TaskId} status", task.Id);
            }
        }
    }

    /// <summary>
    ///     Best-effort reconcile of a task whose claim failed: the guarded write returns a
    ///     Processing (or Pending) row to Pending without bumping the retry count and never touches
    ///     a terminal row. Recovery is then the periodic replayer's job. Errors are logged and
    ///     swallowed so a database hiccup here cannot fault the processor.
    /// </summary>
    protected async Task ReturnToPendingForReplayAsync(PersistedTask task)
    {
        int affected;
        try
        {
            affected = await taskPersistenceService.RequeueStrandedTaskAsync(
                task.Id,
                CancellationToken.None
            );
        }
        catch (Exception dbEx)
        {
            logger.LogError(dbEx, "Failed to reconcile task {TaskId} to Pending", task.Id);
            return;
        }

        if (affected > 0 && task.Status != PersistedTaskStatus.Pending)
        {
            task.Status = PersistedTaskStatus.Pending;
            StatusChanged?.Invoke(task);
        }
    }

    /// <summary>
    ///     Schedules a prompt retry of a task whose transient claim failure was already reconciled
    ///     to Pending, up to <see cref="ClaimRetryBackoff" />'s length times. The backoff is awaited
    ///     off the processor loop so a poisoned task cannot head-of-line block the tasks behind it;
    ///     once the budget is exhausted (or the processor is stopping) the task is left Pending for
    ///     the periodic replayer, so a persistent failure can neither hot-loop nor starve others.
    /// </summary>
    protected Task RequeueTransientClaimFailureAsync(PersistedTask task)
    {
        int attempt = _claimAttempts.AddOrUpdate(task.Id, 1, (_, current) => current + 1);
        if (attempt > ClaimRetryBackoff.Count)
        {
            ForgetClaimAttempts(task.Id);
            logger.LogWarning(
                "Task {TaskId} could not be claimed after {Attempts} attempts; leaving it Pending for the periodic replayer.",
                task.Id,
                attempt
            );
            return Task.CompletedTask;
        }

        TimeSpan backoff = ClaimRetryBackoff[attempt - 1];
        // Use the processor's stopping token, not the per-task one: the per-task cancellation
        // source is disposed as soon as the loop moves on, which would make the detached delay
        // observe a disposed source instead of merely being cancelled on shutdown.
        _ = RetryClaimAfterBackoffAsync(task, backoff, serviceStoppingToken);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Waits <paramref name="backoff" /> and then re-adds the task to its queue, unless the
    ///     processor is stopping or the row is no longer Pending (terminal, claimed elsewhere, or
    ///     removed). Runs detached from the processor loop; every exception is observed so a stale
    ///     timer can neither fault the host nor resurrect a task that already finished.
    /// </summary>
    private async Task RetryClaimAfterBackoffAsync(
        PersistedTask task,
        TimeSpan backoff,
        CancellationToken stoppingToken
    )
    {
        try
        {
            await Task.Delay(backoff, stoppingToken);

            if (stoppingToken.IsCancellationRequested)
            {
                ForgetClaimAttempts(task.Id);
                return;
            }

            // Guarded check: only re-enqueue a row that is still Pending. A terminal, removed or
            // concurrently re-claimed row must not be resurrected or run twice by this timer.
            if (!await taskPersistenceService.IsTaskPendingAsync(task.Id, stoppingToken))
            {
                ForgetClaimAttempts(task.Id);
                return;
            }

            taskQueue.ReEnqueue(task);
        }
        catch (OperationCanceledException)
        {
            // The processor is stopping while we waited. The row is already Pending, so
            // replay/startup recovery owns it; this is not a claim failure.
            ForgetClaimAttempts(task.Id);
        }
        catch (Exception ex)
        {
            // A stale timer must never fault the host; leave the task to the periodic replayer.
            ForgetClaimAttempts(task.Id);
            logger.LogError(ex, "Failed to retry the claim of task {TaskId}", task.Id);
        }
    }

    /// <summary>
    ///     Persists the cancel that <see cref="CancelCurrent" /> requested while the claim was still
    ///     in flight, so the cancellation is not lost now that the task has been dequeued.
    /// </summary>
    protected async Task ApplyClaimCancellationAsync(PersistedTask task)
    {
        bool requeue = serviceStoppingToken.IsCancellationRequested;
        try
        {
            int canceledRows = await taskPersistenceService.CancelTaskAsync(task.Id, requeue);

            // A guarded cancel affects no row when the task already reached a terminal state; the
            // database is the source of truth, so do not advertise a contradictory Canceled status.
            if (canceledRows == 0)
            {
                return;
            }

            task.Status = requeue ? PersistedTaskStatus.Pending : PersistedTaskStatus.Canceled;
            StatusChanged?.Invoke(task);
        }
        catch (Exception dbEx)
        {
            logger.LogError(dbEx, "Failed to update task {TaskId} status", task.Id);
        }
    }
}
