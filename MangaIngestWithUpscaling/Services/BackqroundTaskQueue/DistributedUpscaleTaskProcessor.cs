using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using System.Threading.Channels;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public class DistributedUpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<UpscaleTaskProcessor> logger) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly ChannelReader<PersistedTask> _reader = taskQueue.UpscaleReader;
    private readonly SemaphoreSlim _taskRequested = new(0, 16);

    private readonly Channel<PersistedTask> _tasksDistributionChannel = Channel.CreateUnbounded<PersistedTask>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

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
                dbContext.Update(currentTask);

                runningTasks.Remove(checkAgainst.Id);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;

        _ = Task.Run(async () =>
        {
            var cleanDeadTasksTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (!stoppingToken.IsCancellationRequested && await cleanDeadTasksTimer.WaitForNextTickAsync())
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                using (_lock.EnterScope())
                {
                    List<KeyValuePair<int, PersistedTask>> deadTasks = runningTasks.Where(x =>
                            x.Value.Status == PersistedTaskStatus.Processing
                            && x.Value.LastKeepAlive.AddMinutes(1) < DateTime.UtcNow)
                        .ToList();
                    foreach (var (taskId, task) in deadTasks)
                    {
                        task.Status = PersistedTaskStatus.Failed;
                        _ = StatusChanged?.Invoke(task);
                        dbContext.Update(task);
                        runningTasks.Remove(taskId);
                    }
                }

                await dbContext.SaveChangesAsync();
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _taskRequested.WaitAsync(stoppingToken);
            var task = await _reader.ReadAsync(stoppingToken);
            using (_lock.EnterScope())
            {
                runningTasks[task.Id] = task;
            }

            await _tasksDistributionChannel.Writer.WriteAsync(task);
        }
    }

    public async Task<PersistedTask?> GetTask(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _taskRequested.Release(1);
        var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cancelToken.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var task = await _tasksDistributionChannel.Reader.ReadAsync(cancelToken.Token);
            task.Status = PersistedTaskStatus.Processing;
            task.LastKeepAlive = DateTime.UtcNow.AddSeconds(5); // Bridge network latency
            dbContext.Update(task);
            await dbContext.SaveChangesAsync(cancelToken.Token);
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

    public void TaskCompleted(int taskId)
    {
        using (_lock.EnterScope())
        {
            runningTasks.Remove(taskId);
        }
    }
}