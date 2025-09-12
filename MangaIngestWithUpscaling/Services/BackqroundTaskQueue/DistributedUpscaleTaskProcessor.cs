using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public class DistributedUpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly ChannelReader<PersistedTask> _reader = taskQueue.UpscaleReader;

    private readonly Channel<TaskCompletionSource<PersistedTask>> _taskRequests =
        Channel.CreateUnbounded<TaskCompletionSource<PersistedTask>>();

    private readonly Dictionary<int, PersistedTask> runningTasks = new();
    private PersistedTask? _orphanedTask;
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
                    await taskQueue.RetryAsync(task);
                    _ = StatusChanged?.Invoke(task);
                }
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            TaskCompletionSource<PersistedTask> tcs = await _taskRequests.Reader.ReadAsync(stoppingToken);
            if (tcs.Task.IsCanceled)
            {
                continue;
            }

            if (_orphanedTask != null)
            {
                PersistedTask? taskToGive = _orphanedTask;
                _orphanedTask = null;
                if (tcs.TrySetResult(taskToGive))
                {
                    continue;
                }
                else
                {
                    _orphanedTask = taskToGive;
                }
            }

            try
            {
                PersistedTask task = await _reader.ReadAsync(stoppingToken);

                if (!tcs.TrySetResult(task))
                {
                    _orphanedTask = task;
                }
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(stoppingToken);
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
        await _taskRequests.Writer.WriteAsync(tcs, stoppingToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
        using CancellationTokenRegistration registration = linkedCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            PersistedTask task = await tcs.Task;

            using IServiceScope scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            task.Status = PersistedTaskStatus.Processing;
            task.LastKeepAlive = DateTime.UtcNow.AddSeconds(5); // Bridge network latency

            using (_lock.EnterScope())
            {
                runningTasks[task.Id] = task;
            }

            dbContext.Update(task);
            await dbContext.SaveChangesAsync(stoppingToken);
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

                _ = StatusChanged?.Invoke(task);
            }
        }
    }

    public async Task TaskCompleted(int taskId)
    {
        DateTime time = DateTime.UtcNow;
        using (_lock.EnterScope())
        {
            // Clear orphaned task if it matches the completed task
            if (_orphanedTask != null && _orphanedTask.Id == taskId)
            {
                _orphanedTask = null;
            }

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
}