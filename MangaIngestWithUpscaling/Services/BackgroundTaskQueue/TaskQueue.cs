using System.Threading.Channels;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface ITaskQueue
{
    Task EnqueueAsync<T>(T taskData)
        where T : BaseTask;
    Task RetryAsync(PersistedTask task);
    Task ReorderTaskAsync(PersistedTask task, int newOrder);
    Task RemoveTaskAsync(PersistedTask task);
    Task RemoveTasksAsync(IEnumerable<PersistedTask> tasks);

    Task ReplayPendingOrFailed(CancellationToken cancellationToken = default);

    // Provide live in-memory snapshots for UI without DB reads
    IReadOnlyList<PersistedTask> GetStandardSnapshot();

    IReadOnlyList<PersistedTask> GetUpscaleSnapshot();

    // Convenience to move a task to the front of its queue safely
    Task MoveToFrontAsync(PersistedTask task, CancellationToken cancellationToken = default);
}

public class TaskQueue : ITaskQueue, IHostedService
{
    private readonly ILogger<TaskQueue> _logger;
    private readonly Channel<PersistedTask> _reroutedUpscaleChannel;
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
        var channelOptions = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        };
        _standardChannel = Channel.CreateBounded<PersistedTask>(channelOptions);
        _upscaleChannel = Channel.CreateBounded<PersistedTask>(channelOptions);
        // Reroute channel is unbounded: it's fed only by DistributedUpscaleTaskProcessor and consumed by the local UpscaleTaskProcessor
        _reroutedUpscaleChannel = Channel.CreateUnbounded<PersistedTask>();

        // Initialize sorted sets with a stable comparer
        // NOTE: SortedSet considers items equal when comparer returns 0 and will drop duplicates.
        //       Comparing only by Order collapses many tasks into one when orders match.
        //       Use Order, then Id as a tiebreaker to preserve all tasks with the same Order.
        Comparer<PersistedTask> comparer = Comparer<PersistedTask>.Create(
            (a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                if (byOrder != 0)
                {
                    return byOrder;
                }

                // Id is unique per task (DB identity). This stabilizes ordering and avoids duplicate suppression.
                return a.Id.CompareTo(b.Id);
            }
        );
        _standardTasks = new SortedSet<PersistedTask>(comparer);
        _upscaleTasks = new SortedSet<PersistedTask>(comparer);
    }

    public ChannelReader<PersistedTask> StandardReader => _standardChannel.Reader;
    public ChannelReader<PersistedTask> UpscaleReader => _upscaleChannel.Reader;
    public ChannelReader<PersistedTask> ReroutedUpscaleReader => _reroutedUpscaleChannel.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReplayPendingOrFailed(cancellationToken);

        // Start background processing
        _ = ProcessChannelAsync(
            _standardChannel,
            _standardTasks,
            _standardTasksLock,
            cancellationToken
        );
        _ = ProcessChannelAsync(
            _upscaleChannel,
            _upscaleTasks,
            _upscaleTasksLock,
            cancellationToken
        );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<PersistedTask> GetStandardSnapshot()
    {
        lock (_standardTasksLock)
        {
            // return a shallow copy to avoid external mutation
            return _standardTasks.ToList();
        }
    }

    public IReadOnlyList<PersistedTask> GetUpscaleSnapshot()
    {
        lock (_upscaleTasksLock)
        {
            return _upscaleTasks.ToList();
        }
    }

    public async Task EnqueueAsync<T>(T taskData)
        where T : BaseTask
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Determine the next order value
        int maxOrder = await dbContext.PersistedTasks.MaxAsync(t => (int?)t.Order) ?? 0;
        var taskItem = new PersistedTask { Data = taskData, Order = maxOrder + 1 };

        await dbContext.PersistedTasks.AddAsync(taskItem);
        await dbContext.SaveChangesAsync();

        // Add to the appropriate sorted set
        if (taskData is UpscaleTask or RenameUpscaledChaptersSeriesTask or RepairUpscaleTask)
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
        (SortedSet<PersistedTask> tasks, object lockObj) = task.Data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
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
        PersistedTask? existingTask = await dbContext.PersistedTasks.FirstOrDefaultAsync(t =>
            t.Id == task.Id
        );

        if (existingTask == null)
        {
            return;
        }

        // Update the order and persist changes
        existingTask.Order = newOrder;
        await dbContext.SaveChangesAsync();

        // Notify listeners about the change
        TaskEnqueuedOrChanged?.Invoke(existingTask);
    }

    public async Task MoveToFrontAsync(
        PersistedTask task,
        CancellationToken cancellationToken = default
    )
    {
        // Compute global minimum order from DB to ensure consistent ordering
        using IServiceScope scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        int minOrder =
            await dbContext.PersistedTasks.MinAsync(t => (int?)t.Order, cancellationToken) ?? 0;
        int newOrder = minOrder - 1;
        await ReorderTaskAsync(task, newOrder);
    }

    public async Task RemoveTaskAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        PersistedTask? existingTask = await dbContext.PersistedTasks.FindAsync(task.Id);

        if (existingTask != null)
        {
            dbContext.PersistedTasks.Remove(existingTask);
            await dbContext.SaveChangesAsync();
        }

        var taskToRemove = existingTask ?? task;
        RemoveFromInMemoryCollections(taskToRemove);

        // Notify listeners using the database entity when found; otherwise use the original task instance
        TaskRemoved?.Invoke(taskToRemove);
    }

    public async Task RemoveTasksAsync(IEnumerable<PersistedTask> tasks)
    {
        var taskList = tasks.ToList();
        if (taskList.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Fetch tasks from database to ensure they're tracked and exist
        var taskIds = taskList.Select(t => t.Id).ToHashSet();
        var tasksToRemove = await dbContext
            .PersistedTasks.Where(t => taskIds.Contains(t.Id))
            .ToListAsync();
        var existingIds = tasksToRemove.Select(t => t.Id).ToHashSet();
        var missingTasks = taskList.Where(t => !existingIds.Contains(t.Id)).ToList();

        // Remove all tasks from database in a single transaction
        dbContext.PersistedTasks.RemoveRange(tasksToRemove);
        await dbContext.SaveChangesAsync();

        foreach (var task in tasksToRemove)
        {
            RemoveFromInMemoryCollections(task);
        }

        foreach (var task in missingTasks)
        {
            RemoveFromInMemoryCollections(task);
        }

        // Notify listeners about each removal, including tasks already missing from the database
        foreach (var task in tasksToRemove.Concat(missingTasks))
        {
            TaskRemoved?.Invoke(task);
        }
    }

    public async Task RetryAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        task.Status = PersistedTaskStatus.Pending;
        dbContext.Update(task);
        await dbContext.SaveChangesAsync();

        (SortedSet<PersistedTask> tasks, object lockObj) = task.Data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
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

        // Only consider Pending and retryable Failed tasks here; we will explicitly recover stranded Processing elsewhere
        var pendingTasks = await dbContext
            .PersistedTasks.OrderBy(t => t.Order)
            .AsAsyncEnumerable()
            .Where(t =>
                t.Status == PersistedTaskStatus.Pending
                || (t.Status == PersistedTaskStatus.Failed && t.RetryCount < t.Data.RetryFor)
            )
            .ToListAsync(cancellationToken);

        // Load tasks into sorted sets
        foreach (var task in pendingTasks)
        {
            if (task.Data is UpscaleTask or RenameUpscaledChaptersSeriesTask or RepairUpscaleTask)
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

    // Send tasks that must be handled locally (e.g., Repair/Rename upscaled) directly to the local UpscaleTaskProcessor
    public ValueTask SendToLocalUpscaleAsync(
        PersistedTask task,
        CancellationToken cancellationToken = default
    )
    {
        return _reroutedUpscaleChannel.Writer.WriteAsync(task, cancellationToken);
    }

    public event Func<PersistedTask, Task>? TaskEnqueuedOrChanged;
    public event Func<PersistedTask, Task>? TaskRemoved;

    private async Task ProcessChannelAsync(
        Channel<PersistedTask> channel,
        SortedSet<PersistedTask> tasks,
        object lockObj,
        CancellationToken cancellationToken
    )
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

    private void RemoveFromInMemoryCollections(PersistedTask task)
    {
        bool isUpscaleTask = task.Data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask;

        if (isUpscaleTask)
        {
            lock (_upscaleTasksLock)
            {
                _upscaleTasks.RemoveWhere(t => t.Id == task.Id);
            }
        }
        else
        {
            lock (_standardTasksLock)
            {
                _standardTasks.RemoveWhere(t => t.Id == task.Id);
            }
        }
    }
}
