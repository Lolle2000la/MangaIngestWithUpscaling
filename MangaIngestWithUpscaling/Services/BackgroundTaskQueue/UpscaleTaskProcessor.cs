using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class UpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<UpscaleTaskProcessor> logger) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly TimeSpan _progressDebounce = TimeSpan.FromMilliseconds(250);
    private readonly ChannelReader<PersistedTask> _upscaleReader = taskQueue.UpscaleReader;
    private readonly ChannelReader<PersistedTask> _localUpscaleReader = taskQueue.LocalUpscaleReader;
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
        if (upscalerConfig.Value.RemoteOnly)
        {
            // If the upscaler is configured to run only on the remote worker, we do not start the processor.
            return;
        }

        serviceStoppingToken = stoppingToken;

        while (!stoppingToken.IsCancellationRequested)
        {
            PersistedTask task;
            
            // Use Task.WhenAny to wait for either channel to have data available
            var upscaleReadTask = _upscaleReader.ReadAsync(stoppingToken).AsTask();
            var localUpscaleReadTask = _localUpscaleReader.ReadAsync(stoppingToken).AsTask();

            var completedTask = await Task.WhenAny(upscaleReadTask, localUpscaleReadTask);

            // Cancel the other task that didn't complete to avoid resource leaks
            if (completedTask == upscaleReadTask)
            {
                task = await upscaleReadTask;
                // The localUpscaleReadTask may still be running, but will be automatically cancelled when stoppingToken is cancelled
            }
            else
            {
                task = await localUpscaleReadTask;
                // The upscaleReadTask may still be running, but will be automatically cancelled when stoppingToken is cancelled
            }

            using (_lock.EnterScope())
            {
                currentStoppingToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                currentTask = task;
            }

            await ProcessTaskAsync(task, currentStoppingToken.Token);
        }
    }

    protected async Task ProcessTaskAsync(PersistedTask task, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            task.Status = PersistedTaskStatus.Processing;
            dbContext.Update(task);
            await dbContext.SaveChangesAsync(stoppingToken);
            _ = StatusChanged?.Invoke(task);

            // Forward progress changes to UI by raising StatusChanged (debounced)
            var last = DateTime.UtcNow;
            using var progressSubscription = task.Data.Progress.Changed.Subscribe(e =>
            {
                var now = DateTime.UtcNow;
                if (now - last >= _progressDebounce)
                {
                    last = now;
                    _ = StatusChanged?.Invoke(task);
                }
            });

            await task.Data.ProcessAsync(scope.ServiceProvider, stoppingToken);
            _ = StatusChanged?.Invoke(task);

            task.Status = PersistedTaskStatus.Completed;
            task.ProcessedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(stoppingToken);
            _ = StatusChanged?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {TaskId} was canceled", task.Id);
            // only set to canceled if the cancellation was user requested
            task.Status = serviceStoppingToken.IsCancellationRequested
                ? PersistedTaskStatus.Pending
                : PersistedTaskStatus.Canceled;
            try
            {
                await dbContext.SaveChangesAsync();
                StatusChanged?.Invoke(task);
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to update task {TaskId} status", task.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upscale task {TaskId} failed", task.Id);
            task.Status = PersistedTaskStatus.Failed;
            task.RetryCount++;
            try
            {
                await dbContext.SaveChangesAsync();
                StatusChanged?.Invoke(task);
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to update task {TaskId} status", task.Id);
            }
        }
    }
}