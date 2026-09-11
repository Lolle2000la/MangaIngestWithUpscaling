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
    private CancellationTokenSource? currentStoppingToken;
    private PersistedTask? currentTask;
    private CancellationToken serviceStoppingToken;

    public event Func<PersistedTask, Task>? StatusChanged;

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
        // BackgroundServiceExceptionBehavior.StopHost that would stop the whole application. The row
        // is left untouched so startup recovery can pick it up again.
        bool claimed;
        try
        {
            claimed = await taskPersistenceService.ClaimTaskAsync(task.Id, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Claim of task {TaskId} was canceled", task.Id);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to claim task {TaskId}; leaving it for recovery", task.Id);
            return;
        }

        if (!claimed)
        {
            logger.LogInformation(
                "Task {TaskId} could not be claimed (already processed or concurrency conflict)",
                task.Id
            );
            return;
        }

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

            await taskPersistenceService.CompleteTaskAsync(task.Id);

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
                await taskPersistenceService.CancelTaskAsync(task.Id, requeue);

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
                await taskPersistenceService.FailTaskAsync(task.Id);

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
}
