using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using System;
using System.Threading.Channels;
using ReactiveMarbles.ObservableEvents;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public class StandardTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<StandardTaskProcessor> logger) : BackgroundService
{
    private readonly ChannelReader<PersistedTask> _reader = taskQueue.StandardReader;

    private readonly Lock _lock = new();
    private CancellationTokenSource? currentStoppingToken;
    private PersistedTask? currentTask;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _reader.ReadAsync(stoppingToken);
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
            StatusChanged?.Invoke(task);

            // Polymorphic processing based on concrete type
            await task.Data.ProcessAsync(scope.ServiceProvider, stoppingToken);
            StatusChanged?.Invoke(task);

            task.Status = PersistedTaskStatus.Completed;
            task.ProcessedAt = DateTime.UtcNow;
            dbContext.Update(task);
            await dbContext.SaveChangesAsync(stoppingToken);
            StatusChanged?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {TaskId} was canceled", task.Id);
            task.Status = PersistedTaskStatus.Canceled;
            dbContext.Update(task);
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
            logger.LogError(ex, "Error processing task {TaskId}", task.Id);
            task.Status = PersistedTaskStatus.Failed;
            task.RetryCount++;
            dbContext.Update(task);
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
