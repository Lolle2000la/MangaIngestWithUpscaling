using System.Collections.Concurrent;
using System.Threading.Channels;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class StandardTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<StandardTaskProcessor> logger,
    ITaskPersistenceService taskPersistenceService
) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly TimeSpan _progressDebounce = TimeSpan.FromMilliseconds(250);
    private readonly ChannelReader<object> _reader = taskQueue.StandardReader;

    // Consecutive transient claim failures per task. A poisoned row is retried promptly but a
    // bounded number of times instead of hot-looping or waiting for the periodic replayer.
    // Successful claims and terminal outcomes remove the entry.
    private readonly ConcurrentDictionary<int, int> _claimAttempts = new();
    private CancellationTokenSource? currentStoppingToken;
    private PersistedTask? currentTask;
    private CancellationToken serviceStoppingToken;

    public event Func<PersistedTask, Task>? StatusChanged;

    /// <summary>
    ///     Backoff applied before promptly retrying a transient claim failure. Index 0 is the first
    ///     retry. Once a task has failed more times than there are entries it is left Pending for the
    ///     periodic replayer, bounding retries so a persistent failure cannot hot-loop or starve
    ///     other tasks. Virtual so tests can substitute tiny delays.
    /// </summary>
    protected virtual IReadOnlyList<TimeSpan> ClaimRetryBackoff { get; } =
        new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

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
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;

        // Removal is an authoritative stop signal: if the queue removes the task this processor is
        // running, stop it rather than finish work against a deleted row.
        taskQueue.TaskRemoved += OnTaskRemoved;

        while (!stoppingToken.IsCancellationRequested)
        {
            await _reader.ReadAsync(stoppingToken);
            var task = taskQueue.DequeueStandard();

            if (task == null)
            {
                continue;
            }

            CancellationTokenSource taskStoppingToken;
            using (_lock.EnterScope())
            {
                currentStoppingToken = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken
                );
                taskStoppingToken = currentStoppingToken;
                currentTask = task;
            }

            try
            {
                await ProcessTaskAsync(task, taskStoppingToken.Token);
            }
            finally
            {
                // Dispose the per-task CTS once the task is done so it is not leaked across tasks.
                // Disposal is serialized with CancelCurrent by _lock.
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
        }
    }

    protected async Task ProcessTaskAsync(PersistedTask task, CancellationToken stoppingToken)
    {
        // Claim the task. A claim failure (per-task cancellation from CancelCurrent/removal, or a
        // transient database error) must not fault ExecuteAsync: under
        // BackgroundServiceExceptionBehavior.StopHost that would stop the whole application.
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
            _claimAttempts.TryRemove(task.Id, out _);
            await ApplyClaimCancellationAsync(task);
            return;
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
            await RequeueTransientClaimFailureAsync(task, stoppingToken);
            return;
        }

        if (!claimed)
        {
            // The row is no longer Pending (already claimed elsewhere or terminal), so dropping the
            // in-memory copy is correct: re-adding it would spin on a task this processor cannot own.
            logger.LogInformation(
                "Task {TaskId} could not be claimed (already processed or concurrency conflict)",
                task.Id
            );
            _claimAttempts.TryRemove(task.Id, out _);
            return;
        }

        // The claim succeeded, so any earlier transient failures are resolved.
        _claimAttempts.TryRemove(task.Id, out _);
        task.Status = PersistedTaskStatus.Processing;
        StatusChanged?.Invoke(task);

        using var scope = scopeFactory.CreateScope();

        try
        {
            // Polymorphic processing based on concrete type, forward debounced progress to UI
            var last = DateTime.UtcNow;
            using var progressSubscription = task.Data.Progress.Changed.Subscribe(_ =>
            {
                var now = DateTime.UtcNow;
                if (now - last >= _progressDebounce)
                {
                    last = now;
                    var _discardTick = StatusChanged?.Invoke(task);
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
            logger.LogError(ex, "Error processing task {TaskId}", task.Id);
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
    private async Task ReturnToPendingForReplayAsync(PersistedTask task)
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
    ///     Re-adds a task whose transient claim failure was already reconciled to Pending so the
    ///     processor loop retries it promptly, up to <see cref="ClaimRetryBackoff"/>'.Length times.
    ///     Once the budget is exhausted (or the processor is stopping) the task is left Pending for
    ///     the periodic replayer, so a persistent failure can neither hot-loop nor starve others.
    /// </summary>
    private async Task RequeueTransientClaimFailureAsync(
        PersistedTask task,
        CancellationToken stoppingToken
    )
    {
        int attempt = _claimAttempts.AddOrUpdate(task.Id, 1, (_, current) => current + 1);
        if (attempt > ClaimRetryBackoff.Count)
        {
            _claimAttempts.TryRemove(task.Id, out _);
            logger.LogWarning(
                "Task {TaskId} could not be claimed after {Attempts} attempts; leaving it Pending for the periodic replayer.",
                task.Id,
                attempt
            );
            return;
        }

        try
        {
            await Task.Delay(ClaimRetryBackoff[attempt - 1], stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The processor is stopping or the task was cancelled while we waited. The row is
            // already Pending, so replay/startup recovery owns it; this is not a claim failure.
            _claimAttempts.TryRemove(task.Id, out _);
            return;
        }

        taskQueue.ReEnqueue(task);
    }

    /// <summary>
    ///     Persists the cancel that <see cref="CancelCurrent" /> requested while the claim was still
    ///     in flight, so the cancellation is not lost now that the task has been dequeued.
    /// </summary>
    private async Task ApplyClaimCancellationAsync(PersistedTask task)
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
