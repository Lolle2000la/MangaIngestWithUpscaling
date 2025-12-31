using System.Threading.Channels;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class StandardTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<StandardTaskProcessor> logger,
    ITaskPersistenceService taskPersistenceService,
    ITaskSerializer taskSerializer
) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly TimeSpan _progressDebounce = TimeSpan.FromMilliseconds(250);
    private readonly ChannelReader<PersistedTask> _reader = taskQueue.StandardReader;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _reader.ReadAsync(stoppingToken);
            using (_lock.EnterScope())
            {
                currentStoppingToken = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken
                );
                currentTask = task;
            }

            await ProcessTaskAsync(task, currentStoppingToken.Token);
        }
    }

    protected async Task ProcessTaskAsync(PersistedTask task, CancellationToken stoppingToken)
    {
        // Claim the task
        if (!await taskPersistenceService.ClaimTaskAsync(task.Id, stoppingToken))
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
            var data = taskSerializer.Deserialize(task);

            using var progressSubscription = data.Progress.Changed.Subscribe(_ =>
            {
                var now = DateTime.UtcNow;
                if (now - last >= _progressDebounce)
                {
                    last = now;
                    var _discardTick = StatusChanged?.Invoke(task);
                }
            });

            await data.ProcessAsync(scope.ServiceProvider, stoppingToken);
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
