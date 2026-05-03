using System.Threading.Channels;
using AutoRegisterInject;
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

    Task ReplayPendingOrFailed(CancellationToken cancellationToken = default);

    // Provide live in-memory snapshots for UI without DB reads
    IReadOnlyList<PersistedTask> GetStandardSnapshot();

    IReadOnlyList<PersistedTask> GetUpscaleSnapshot();

    // Convenience to move a task to the front of its queue safely
    Task MoveToFrontAsync(PersistedTask task, CancellationToken cancellationToken = default);
}

public class TaskQueue : ITaskQueue, IHostedService
{
    private static readonly object Signal = new();
    private readonly ILogger<TaskQueue> _logger;
    private readonly Channel<PersistedTask> _reroutedUpscaleChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<object> _standardChannel;

    private readonly SortedSet<PersistedTask> _standardTasks;
    private readonly object _standardTasksLock = new();
    private readonly Channel<object> _upscaleChannel;
    private readonly SortedSet<PersistedTask> _upscaleTasks;
    private readonly object _upscaleTasksLock = new();

    public TaskQueue(IServiceScopeFactory scopeFactory, ILogger<TaskQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Use unbounded channels for signals; signals do not carry tasks, but notify consumers to pull from sorted sets.
        _standardChannel = Channel.CreateUnbounded<object>();
        _upscaleChannel = Channel.CreateUnbounded<object>();
        // Reroute channel remains carrying tasks: it's fed only by DistributedUpscaleTaskProcessor and consumed by the local UpscaleTaskProcessor
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

    public ChannelReader<object> StandardReader => _standardChannel.Reader;
    public ChannelReader<object> UpscaleReader => _upscaleChannel.Reader;
    public ChannelReader<PersistedTask> ReroutedUpscaleReader => _reroutedUpscaleChannel.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReplayPendingOrFailed(cancellationToken);
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

        // Add to the appropriate sorted set and notify via signal
        if (
            taskData
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
                or DetectSplitCandidatesTask
                or ApplySplitsTask
        )
        {
            bool added;
            lock (_upscaleTasksLock)
            {
                added = _upscaleTasks.Add(taskItem);
            }
            if (added)
            {
                await _upscaleChannel.Writer.WriteAsync(Signal);
            }
        }
        else
        {
            bool added;
            lock (_standardTasksLock)
            {
                added = _standardTasks.Add(taskItem);
            }
            if (added)
            {
                await _standardChannel.Writer.WriteAsync(Signal);
            }
        }

        TaskEnqueuedOrChanged?.Invoke(taskItem);

        var queueCleanup = scope.ServiceProvider.GetRequiredService<IQueueCleanup>();
        var removedTaskIds = await queueCleanup.CleanupAsync();

        if (TaskRemoved != null && removedTaskIds.Count > 0)
        {
            await Task.WhenAll(
                removedTaskIds.Select(id => TaskRemoved(new PersistedTask { Id = id }))
            );
        }
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
                or DetectSplitCandidatesTask
                or ApplySplitsTask
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

        dbContext.PersistedTasks.Remove(task);
        await dbContext.SaveChangesAsync();

        (SortedSet<PersistedTask> tasks, object lockObj) = task.Data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
                or DetectSplitCandidatesTask
                or ApplySplitsTask
            ? (_upscaleTasks, _upscaleTasksLock)
            : (_standardTasks, _standardTasksLock);

        lock (lockObj)
        {
            var toRemove = tasks.FirstOrDefault(t => t.Id == task.Id);
            if (toRemove != null)
                tasks.Remove(toRemove);
        }

        // Notify listeners about the removal
        TaskRemoved?.Invoke(task);
    }

    public async Task RetryAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        task.Status = PersistedTaskStatus.Pending;
        dbContext.Update(task);
        await dbContext.SaveChangesAsync();

        if (
            task.Data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
                or DetectSplitCandidatesTask
                or ApplySplitsTask
        )
        {
            bool added;
            lock (_upscaleTasksLock)
            {
                added = _upscaleTasks.Add(task);
            }
            if (added)
            {
                await _upscaleChannel.Writer.WriteAsync(Signal);
            }
        }
        else
        {
            bool added;
            lock (_standardTasksLock)
            {
                added = _standardTasks.Add(task);
            }
            if (added)
            {
                await _standardChannel.Writer.WriteAsync(Signal);
            }
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

        // Load tasks into sorted sets and signal their presence
        foreach (var task in pendingTasks)
        {
            if (
                task.Data
                is UpscaleTask
                    or RenameUpscaledChaptersSeriesTask
                    or RepairUpscaleTask
                    or DetectSplitCandidatesTask
                    or ApplySplitsTask
            )
            {
                bool added;
                lock (_upscaleTasksLock)
                {
                    added = _upscaleTasks.Add(task);
                }
                if (added)
                {
                    _upscaleChannel.Writer.TryWrite(Signal);
                }
            }
            else
            {
                bool added;
                lock (_standardTasksLock)
                {
                    added = _standardTasks.Add(task);
                }
                if (added)
                {
                    _standardChannel.Writer.TryWrite(Signal);
                }
            }
        }
    }

    public PersistedTask? DequeueStandard()
    {
        lock (_standardTasksLock)
        {
            if (_standardTasks.Count == 0)
                return null;
            var task = _standardTasks.Min;
            _standardTasks.Remove(task!);
            return task;
        }
    }

    public PersistedTask? DequeueUpscale()
    {
        lock (_upscaleTasksLock)
        {
            if (_upscaleTasks.Count == 0)
                return null;
            var task = _upscaleTasks.Min;
            _upscaleTasks.Remove(task!);
            return task;
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
}
