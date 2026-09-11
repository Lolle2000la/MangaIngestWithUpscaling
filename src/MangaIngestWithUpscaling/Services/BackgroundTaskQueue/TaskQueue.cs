using System.Threading.Channels;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
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

    /// <summary>
    ///     Removes several tasks in a single database round trip and raises a single removal
    ///     notification per task.
    /// </summary>
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
    private static readonly object Signal = new();
    private readonly ILogger<TaskQueue> _logger;
    private readonly Channel<PersistedTask> _reroutedUpscaleChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<object> _standardChannel;
    private readonly SemaphoreSlim _enqueueSemaphore = new(1, 1);

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

    public static bool IsUpscaleTask(BaseTask data) =>
        data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
                or DetectSplitCandidatesTask
                or ApplySplitsTask;

    public async Task EnqueueAsync<T>(T taskData)
        where T : BaseTask
    {
        // The semaphore serializes order computation and is held across the event invocation below.
        // Subscribers (currently only TaskRegistry, which is synchronous) must NOT call back into
        // this method or block on it, otherwise the queue deadlocks.
        await _enqueueSemaphore.WaitAsync();
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            bool isUpscale = IsUpscaleTask(taskData);
            int? newChapterId = GetChapterId(taskData);
            int? targetOrder = null;
            Chapter? newChapter = null;

            if (newChapterId.HasValue)
            {
                newChapter = await dbContext.Chapters.FirstOrDefaultAsync(c =>
                    c.Id == newChapterId.Value
                );
            }

            if (newChapter != null)
            {
                List<PersistedTask> queueTasks;
                if (isUpscale)
                {
                    lock (_upscaleTasksLock)
                    {
                        queueTasks = _upscaleTasks.ToList();
                    }
                }
                else
                {
                    lock (_standardTasksLock)
                    {
                        queueTasks = _standardTasks.ToList();
                    }
                }

                var activeChapterIds = queueTasks
                    .Select(t => GetChapterId(t.Data))
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();

                var chaptersMap = await dbContext
                    .Chapters.Where(c => activeChapterIds.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id);

                foreach (var t in queueTasks)
                {
                    int? existChapterId = GetChapterId(t.Data);
                    if (
                        existChapterId.HasValue
                        && chaptersMap.TryGetValue(existChapterId.Value, out var existChapter)
                    )
                    {
                        if (CompareChapters(newChapter, existChapter) < 0)
                        {
                            targetOrder = t.Order;
                            break;
                        }
                    }
                }
            }

            int finalOrder;
            if (targetOrder.HasValue)
            {
                finalOrder = targetOrder.Value;

                var tasksToShift = await dbContext
                    .PersistedTasks.Where(t =>
                        t.Order >= finalOrder
                        && (
                            t.Status == PersistedTaskStatus.Pending
                            || t.Status == PersistedTaskStatus.Failed
                            || t.Status == PersistedTaskStatus.Canceled
                        )
                    )
                    .ToListAsync();

                foreach (var t in tasksToShift)
                {
                    t.Order += 1;
                }
            }
            else
            {
                int maxOrder = await dbContext.PersistedTasks.MaxAsync(t => (int?)t.Order) ?? 0;
                finalOrder = maxOrder + 1;
            }

            var taskItem = new PersistedTask { Data = taskData, Order = finalOrder };
            await dbContext.PersistedTasks.AddAsync(taskItem);
            await dbContext.SaveChangesAsync();

            var shiftedTasks = new List<PersistedTask>();
            bool added = false;
            lock (_upscaleTasksLock)
            {
                lock (_standardTasksLock)
                {
                    if (targetOrder.HasValue)
                    {
                        var upscaleToUpdate = _upscaleTasks
                            .Where(t => t.Order >= targetOrder.Value)
                            .ToList();
                        foreach (var t in upscaleToUpdate)
                        {
                            _upscaleTasks.Remove(t);
                        }
                        foreach (var t in upscaleToUpdate)
                        {
                            t.Order += 1;
                            _upscaleTasks.Add(t);
                        }
                        shiftedTasks.AddRange(upscaleToUpdate);

                        var standardToUpdate = _standardTasks
                            .Where(t => t.Order >= targetOrder.Value)
                            .ToList();
                        foreach (var t in standardToUpdate)
                        {
                            _standardTasks.Remove(t);
                        }
                        foreach (var t in standardToUpdate)
                        {
                            t.Order += 1;
                            _standardTasks.Add(t);
                        }
                        shiftedTasks.AddRange(standardToUpdate);
                    }

                    if (isUpscale)
                    {
                        added = _upscaleTasks.Add(taskItem);
                    }
                    else
                    {
                        added = _standardTasks.Add(taskItem);
                    }
                }
            }

            if (added)
            {
                if (isUpscale)
                {
                    await _upscaleChannel.Writer.WriteAsync(Signal);
                }
                else
                {
                    await _standardChannel.Writer.WriteAsync(Signal);
                }
            }

            // Snapshot the event delegates once: a concurrent unsubscribe (e.g. TaskRegistry.StopAsync)
            // can null the field between reads, which would cause a NullReferenceException.
            var enqueuedHandler = TaskEnqueuedOrChanged;
            if (enqueuedHandler != null)
            {
                foreach (var t in shiftedTasks)
                {
                    await enqueuedHandler.Invoke(t);
                }
                await enqueuedHandler.Invoke(taskItem);
            }

            var queueCleanup = scope.ServiceProvider.GetRequiredService<IQueueCleanup>();
            var removedTaskIds = await queueCleanup.CleanupAsync();

            var removedHandler = TaskRemoved;
            if (removedHandler != null && removedTaskIds.Count > 0)
            {
                await Task.WhenAll(
                    removedTaskIds.Select(id => removedHandler(new PersistedTask { Id = id }))
                );
            }
        }
        finally
        {
            _enqueueSemaphore.Release();
        }
    }

    public async Task ReorderTaskAsync(PersistedTask task, int newOrder)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Determine which set to modify
        (SortedSet<PersistedTask> tasks, object lockObj) = IsUpscaleTask(task.Data)
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

        // Only delete if the row still exists; a concurrent removal or cleanup would otherwise
        // make SaveChanges throw DbUpdateConcurrencyException.
        PersistedTask? existing = await dbContext.PersistedTasks.FirstOrDefaultAsync(t =>
            t.Id == task.Id
        );
        if (existing is not null)
        {
            dbContext.PersistedTasks.Remove(existing);
            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent remover (another call or QueueCleanup) deleted the row between our
                // check and our SaveChanges. The desired end state — the row is gone — already
                // holds, so treat the removal as successful instead of propagating.
                _logger.LogDebug(
                    "Task {TaskId} was already removed by a concurrent operation.",
                    task.Id
                );
            }
        }

        RemoveFromInMemoryQueues(task.Id);

        // Notify listeners about the removal
        await RaiseTaskRemovedAsync(task);
    }

    public async Task RemoveTasksAsync(IEnumerable<PersistedTask> tasks)
    {
        List<PersistedTask> list = tasks as List<PersistedTask> ?? tasks.ToList();
        if (list.Count == 0)
            return;

        List<int> ids = list.Select(t => t.Id).Distinct().ToList();

        using IServiceScope scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await DeleteExistingRowsAsync(dbContext, ids);

        foreach (var task in list)
        {
            RemoveFromInMemoryQueues(task.Id);
            await RaiseTaskRemovedAsync(task);
        }
    }

    /// <summary>
    ///     Deletes the rows for <paramref name="ids" /> that still exist. A concurrent remover
    ///     (another call or <see cref="IQueueCleanup" />) can delete a subset of the batch between
    ///     the existence check and <c>SaveChanges</c>, which makes EF throw
    ///     <see cref="DbUpdateConcurrencyException" /> and roll back the whole delete. On a conflict
    ///     the change tracker is discarded and the still-existing rows are reloaded and retried, so
    ///     no surviving row is silently skipped.
    /// </summary>
    private async Task DeleteExistingRowsAsync(ApplicationDbContext dbContext, List<int> ids)
    {
        // Each conflict means at least one requested row was concurrently deleted, so progress is
        // guaranteed for at most one failed attempt per id (the +1 covers the successful attempt).
        for (int attempt = 0; attempt <= ids.Count; attempt++)
        {
            var existing = new List<PersistedTask>(ids.Count);
            foreach (int[] chunk in ids.Chunk(500))
            {
                existing.AddRange(
                    await dbContext.PersistedTasks.Where(t => chunk.Contains(t.Id)).ToListAsync()
                );
            }

            if (existing.Count == 0)
            {
                return;
            }

            dbContext.PersistedTasks.RemoveRange(existing);
            try
            {
                await dbContext.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogDebug(
                    ex,
                    "One or more of the {Count} requested tasks were already removed by a concurrent operation; retrying the remaining rows.",
                    existing.Count
                );

                // The transaction rolled back, so the change tracker cannot be trusted. Detach the
                // tracked tasks and let the next attempt reload the current database state; rows
                // deleted concurrently are simply not found again.
                foreach (
                    var entry in dbContext
                        .ChangeTracker.Entries<PersistedTask>()
                        .Where(e =>
                            e.State != EntityState.Unchanged && e.State != EntityState.Detached
                        )
                        .ToList()
                )
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        _logger.LogError(
            "Failed to delete {Count} background tasks after repeated concurrency conflicts.",
            ids.Count
        );
    }

    /// <summary>
    ///     Awaits every <see cref="TaskRemoved" /> subscriber, observing each handler's exception so
    ///     one faulty handler cannot prevent the others from running.
    /// </summary>
    private async Task RaiseTaskRemovedAsync(PersistedTask task)
    {
        Delegate[] handlers = TaskRemoved?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate handler in handlers)
        {
            try
            {
                await ((Func<PersistedTask, Task>)handler).Invoke(task);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TaskRemoved handler failed for task {TaskId}.", task.Id);
            }
        }
    }

    /// <summary>
    ///     Removes a task from whichever in-memory queue currently holds it. Looking the task up by
    ///     id avoids depending on <see cref="PersistedTask.Data" />, which may be unavailable for
    ///     id-only removal notifications.
    /// </summary>
    private void RemoveFromInMemoryQueues(int taskId)
    {
        lock (_upscaleTasksLock)
        {
            var upscale = _upscaleTasks.FirstOrDefault(t => t.Id == taskId);
            if (upscale != null)
            {
                _upscaleTasks.Remove(upscale);
                return;
            }
        }

        lock (_standardTasksLock)
        {
            var standard = _standardTasks.FirstOrDefault(t => t.Id == taskId);
            if (standard != null)
            {
                _standardTasks.Remove(standard);
            }
        }
    }

    public async Task RetryAsync(PersistedTask task)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        task.Status = PersistedTaskStatus.Pending;
        dbContext.Update(task);
        await dbContext.SaveChangesAsync();

        if (IsUpscaleTask(task.Data))
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
        // Filter by Status in DB to avoid full table scan
        var candidateTasks = await dbContext
            .PersistedTasks.Where(t =>
                t.Status == PersistedTaskStatus.Pending || t.Status == PersistedTaskStatus.Failed
            )
            .OrderBy(t => t.Order)
            .ToListAsync(cancellationToken);

        var pendingTasks = candidateTasks
            .Where(t =>
                t.Status == PersistedTaskStatus.Pending
                || (t.Status == PersistedTaskStatus.Failed && t.RetryCount < t.Data.RetryFor)
            )
            .ToList();

        // Load tasks into sorted sets and signal their presence
        bool dbChanged = false;
        var changedTasks = new List<PersistedTask>();
        foreach (var task in pendingTasks)
        {
            if (task.Status == PersistedTaskStatus.Failed)
            {
                task.Status = PersistedTaskStatus.Pending;
                dbContext.Update(task);
                dbChanged = true;
                changedTasks.Add(task);
            }
        }

        if (dbChanged)
        {
            // Save status changes before they are added to the in-memory queue and signaled.
            // This prevents a processor from seeing the old 'Failed' status if it wakes up immediately.
            await dbContext.SaveChangesAsync(cancellationToken);

            // Notify listeners (TaskRegistry/UI) that Failed tasks are now Pending
            foreach (var task in changedTasks)
            {
                TaskEnqueuedOrChanged?.Invoke(task);
            }
        }

        foreach (var task in pendingTasks)
        {
            if (IsUpscaleTask(task.Data))
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

    public PersistedTask? PeekUpscale()
    {
        lock (_upscaleTasksLock)
        {
            return _upscaleTasks.Count == 0 ? null : _upscaleTasks.Min;
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

    private static readonly NaturalSortComparer<Chapter> _chapterNameComparer = new(c =>
        c.FileName
    );

    private static int? GetChapterId(BaseTask data)
    {
        return data is IChapterTask chapterTask ? chapterTask.ChapterId : null;
    }

    private static int CompareChapters(Chapter a, Chapter b)
    {
        if (a.MangaId != b.MangaId)
        {
            return 0;
        }

        var numAStr = ChapterNumberHelper.ExtractChapterNumber(a.FileName);
        var numBStr = ChapterNumberHelper.ExtractChapterNumber(b.FileName);

        if (
            numAStr != null
            && numBStr != null
            && decimal.TryParse(
                numAStr,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal numA
            )
            && decimal.TryParse(
                numBStr,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal numB
            )
        )
        {
            int comparison = numA.CompareTo(numB);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _chapterNameComparer.Compare(a, b);
    }
}
