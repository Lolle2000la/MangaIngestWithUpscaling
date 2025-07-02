using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public interface ITaskQueue
{
    Task EnqueueAsync<T>(T taskData) where T : BaseTask;
    Task RetryAsync(PersistedTask task);
    Task ReorderTaskAsync(PersistedTask task, int newOrder);
    Task RemoveTaskAsync(PersistedTask task);
    Task ReplayPendingOrFailed(CancellationToken cancellationToken = default);
}

public class TaskQueue : ITaskQueue, IHostedService
{
    private readonly ILogger<TaskQueue> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<PersistedTask> _standardChannel;

    private readonly SortedSet<PersistedTask> _standardTasks;
    private readonly object _standardTasksLock = new();
    private readonly Channel<PersistedTask> _upscaleChannel;
    private readonly SortedSet<PersistedTask> _upscaleTasks;
    private readonly object _upscaleTasksLock = new();

    public TaskQueue(IServiceScopeFactory scopeFactory, ILogger<TaskQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Create bounded channels to control concurrency
        var channelOptions = new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait };
        _standardChannel = Channel.CreateBounded<PersistedTask>(channelOptions);
        _upscaleChannel = Channel.CreateBounded<PersistedTask>(channelOptions);

        // Initialize sorted sets with order comparer
        Comparer<PersistedTask> comparer = Comparer<PersistedTask>.Create((a, b) => a.Order.CompareTo(b.Order));
        _standardTasks = new SortedSet<PersistedTask>(comparer);
        _upscaleTasks = new SortedSet<PersistedTask>(comparer);
    }

    public ChannelReader<PersistedTask> StandardReader => _standardChannel.Reader;
    public ChannelReader<PersistedTask> UpscaleReader => _upscaleChannel.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReplayPendingOrFailed(cancellationToken);

        // Start background processing
        _ = ProcessChannelAsync(_standardChannel, _standardTasks, _standardTasksLock, cancellationToken);
        _ = ProcessChannelAsync(_upscaleChannel, _upscaleTasks, _upscaleTasksLock, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task EnqueueAsync<T>(T taskData) where T : BaseTask
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Determine the next order value
        int maxOrder = await dbContext.PersistedTasks.MaxAsync(t => (int?)t.Order) ?? 0;
        var taskItem = new PersistedTask { Data = taskData, Order = maxOrder + 1 };

        await dbContext.PersistedTasks.AddAsync(taskItem);
        await dbContext.SaveChangesAsync();

        // Add to the appropriate sorted set
        if (taskData is UpscaleTask or RenameUpscaledChaptersSeriesTask)
        {
            lock (_upscaleTasksLock)
            {
                _upscaleTasks.Add(taskItem);
            }
        }
        else
        {
            lock (_standardTasksLock)
            {
                _standardTasks.Add(taskItem);
            }
        }

        TaskEnqueuedOrChanged?.Invoke(taskItem);

        var queueCleanup = scope.ServiceProvider.GetRequiredService<IQueueCleanup>();
        await queueCleanup.CleanupAsync();
    }

    public async Task ReorderTaskAsync(PersistedTask task, int newOrder)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Determine which set to modify
        var (tasks, lockObj) = task.Data is UpscaleTask or RenameUpscaledChaptersSeriesTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            // Find the task in the set by ID
            PersistedTask? taskInSet = tasks.FirstOrDefault(t => t.Id == task.Id);

            if (taskInSet != null)
            {
                // Remove and re-add to update sorting
                tasks.Remove(taskInSet);
                taskInSet.Order = newOrder; // Update order in the tracked object
                tasks.Add(taskInSet);
            }
        }

        // Fetch the task from the database to ensure we have the latest version
        PersistedTask? existingTask = await dbContext.PersistedTasks
            .FirstOrDefaultAsync(t => t.Id == task.Id);

        if (existingTask == null)
        {
            return;
        }

        // Update the order and persist changes
        existingTask.Order = newOrder;
        await dbContext.SaveChangesAsync();
    }

    public async Task RemoveTaskAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        dbContext.PersistedTasks.Remove(task);
        await dbContext.SaveChangesAsync();

        var (tasks, lockObj) = task.Data is UpscaleTask or RenameUpscaledChaptersSeriesTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            var toRemove = tasks.FirstOrDefault(t => t.Id == task.Id);
            if (toRemove != null)
                tasks.Remove(toRemove);
        }
    }

    public async Task RetryAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        task.Status = PersistedTaskStatus.Pending;
        dbContext.Update(task);
        await dbContext.SaveChangesAsync();

        var (tasks, lockObj) = task.Data is UpscaleTask or RenameUpscaledChaptersSeriesTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            tasks.Add(task);
        }

        TaskEnqueuedOrChanged?.Invoke(task);
    }

    public async Task ReplayPendingOrFailed(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pendingTasks = await dbContext.PersistedTasks
            .OrderBy(t => t.Order)
            .AsAsyncEnumerable()
            .Where(t => t.Status == PersistedTaskStatus.Pending || t.Status == PersistedTaskStatus.Processing
                                                                || (t.Status == PersistedTaskStatus.Failed &&
                                                                    t.RetryCount < t.Data.RetryFor))
            .ToListAsync(cancellationToken);

        // Reset processing tasks to pending
        foreach (var task in pendingTasks)
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                task.Status = PersistedTaskStatus.Pending;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Load tasks into sorted sets
        foreach (var task in pendingTasks)
        {
            if (task.Data is UpscaleTask or RenameUpscaledChaptersSeriesTask)
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
    }

    public event Func<PersistedTask, Task>? TaskEnqueuedOrChanged;

    private async Task ProcessChannelAsync(
        Channel<PersistedTask> channel,
        SortedSet<PersistedTask> tasks,
        object lockObj,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PersistedTask? task = null;
            lock (lockObj)
            {
                if (tasks.Count > 0)
                {
                    task = tasks.Min;
                    tasks.Remove(task!);
                }
            }

            if (task != null)
            {
                await channel.Writer.WriteAsync(task, cancellationToken);
            }
            else
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}