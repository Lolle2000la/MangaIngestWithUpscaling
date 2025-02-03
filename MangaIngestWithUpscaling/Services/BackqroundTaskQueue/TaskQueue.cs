using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using System.Threading.Channels;
using System;
using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public interface ITaskQueue
{
    Task EnqueueAsync<T>(T taskData) where T : BaseTask;
    Task RetryAsync(PersistedTask task);
    Task ReorderTaskAsync(PersistedTask task, int newOrder);
    Task RemoveTaskAsync(PersistedTask task);
}

public class TaskQueue : ITaskQueue, IHostedService
{
    private readonly Channel<PersistedTask> _standardChannel;
    private readonly Channel<PersistedTask> _upscaleChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskQueue> _logger;

    private readonly SortedSet<PersistedTask> _standardTasks;
    private readonly SortedSet<PersistedTask> _upscaleTasks;
    private readonly object _standardTasksLock = new object();
    private readonly object _upscaleTasksLock = new object();

    public ChannelReader<PersistedTask> StandardReader => _standardChannel.Reader;
    public ChannelReader<PersistedTask> UpscaleReader => _upscaleChannel.Reader;

    public event Func<PersistedTask, Task>? TaskEnqueuedOrChanged;

    public TaskQueue(IServiceScopeFactory scopeFactory, ILogger<TaskQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Create bounded channels to control concurrency
        var channelOptions = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _standardChannel = Channel.CreateBounded<PersistedTask>(channelOptions);
        _upscaleChannel = Channel.CreateBounded<PersistedTask>(channelOptions);

        // Initialize sorted sets with order comparer
        var comparer = Comparer<PersistedTask>.Create((a, b) => a.Order.CompareTo(b.Order));
        _standardTasks = new SortedSet<PersistedTask>(comparer);
        _upscaleTasks = new SortedSet<PersistedTask>(comparer);
    }

    public async Task EnqueueAsync<T>(T taskData) where T : BaseTask
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Determine the next order value
        int maxOrder = await dbContext.PersistedTasks.MaxAsync(t => (int?)t.Order) ?? 0;
        var taskItem = new PersistedTask { Data = taskData, Order = maxOrder + 1 };

        await dbContext.PersistedTasks.AddAsync(taskItem);
        await dbContext.SaveChangesAsync();

        // Add to the appropriate sorted set
        if (taskData is UpscaleTask)
        {
            lock (_upscaleTasksLock)
                _upscaleTasks.Add(taskItem);
        }
        else
        {
            lock (_standardTasksLock)
                _standardTasks.Add(taskItem);
        }

        TaskEnqueuedOrChanged?.Invoke(taskItem);

        // Existing cleanup code...
        var oldTasks = await dbContext.PersistedTasks
            .Where(t => t.Status == PersistedTaskStatus.Completed)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(100)
            .ToListAsync();

        if (oldTasks.Count > 25)
            _logger.LogInformation("Cleaning up {TaskCount} old tasks.", oldTasks.Count);

        dbContext.PersistedTasks.RemoveRange(oldTasks);
        await dbContext.SaveChangesAsync();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pendingTasks = await dbContext.PersistedTasks
            .Where(t => t.Status == PersistedTaskStatus.Pending || t.Status == PersistedTaskStatus.Processing)
            .OrderBy(t => t.Order)
            .ToListAsync(cancellationToken);

        // Reset processing tasks to pending
        foreach (var task in pendingTasks)
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                task.Status = PersistedTaskStatus.Pending;
                dbContext.Update(task);
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        // Load tasks into sorted sets
        foreach (var task in pendingTasks)
        {
            if (task.Data is UpscaleTask)
            {
                lock (_upscaleTasksLock)
                    _upscaleTasks.Add(task);
            }
            else
            {
                lock (_standardTasksLock)
                    _standardTasks.Add(task);
            }
        }

        // Start background processing
        _ = ProcessChannelAsync(_standardChannel, _standardTasks, _standardTasksLock, cancellationToken);
        _ = ProcessChannelAsync(_upscaleChannel, _upscaleTasks, _upscaleTasksLock, cancellationToken);
    }

    private async Task ProcessChannelAsync(
        Channel<PersistedTask> channel,
        SortedSet<PersistedTask> tasks,
        object lockObj,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PersistedTask task = null;
            lock (lockObj)
            {
                if (tasks.Count > 0)
                    task = tasks.Min;
            }

            if (task != null)
            {
                await channel.Writer.WriteAsync(task, cancellationToken);
                lock (lockObj)
                {
                    tasks.Remove(task);
                }
            }
            else
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ReorderTaskAsync(PersistedTask task, int newOrder)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Fetch the task from the database to ensure we have the latest version
        var existingTask = await dbContext.PersistedTasks
            .FirstOrDefaultAsync(t => t.Id == task.Id);

        if (existingTask == null) return;

        // Update the order and persist changes
        existingTask.Order = newOrder;
        dbContext.PersistedTasks.Update(existingTask);
        await dbContext.SaveChangesAsync();

        // Determine which set to modify
        var (tasks, lockObj) = existingTask.Data is UpscaleTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            // Find the task in the set by ID (works across different instances)
            var taskInSet = tasks.FirstOrDefault(t => t.Id == existingTask.Id);

            if (taskInSet != null)
            {
                // Remove and re-add to update sorting
                tasks.Remove(taskInSet);
                taskInSet.Order = newOrder; // Update order in the tracked object
                tasks.Add(taskInSet);
            }
        }
    }

    public async Task RemoveTaskAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        dbContext.PersistedTasks.Remove(task);
        await dbContext.SaveChangesAsync();

        var (tasks, lockObj) = task.Data is UpscaleTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            var toRemove = tasks.FirstOrDefault(t => t.Id == task.Id);
            tasks.Remove(toRemove);
        }
    }

    public async Task RetryAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        task.Status = PersistedTaskStatus.Pending;
        await dbContext.SaveChangesAsync();

        var (tasks, lockObj) = task.Data is UpscaleTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            tasks.Add(task);
        }

        TaskEnqueuedOrChanged?.Invoke(task);
    }
}