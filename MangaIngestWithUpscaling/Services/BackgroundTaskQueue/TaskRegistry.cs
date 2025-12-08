using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Central registry of all background tasks with live, read-only filtered/sorted views.
///     Keeps UI consistent without requiring a UI DbContext.
/// </summary>
public class TaskRegistry : IHostedService, IDisposable
{
    private readonly CompositeDisposable _cleanups = new();
    private readonly DistributedUpscaleTaskProcessor _distributedUpscaleProcessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StandardTaskProcessor _standardProcessor;
    private readonly TaskQueue _taskQueue;
    private readonly SourceCache<PersistedTask, int> _tasks = new(x => x.Id);
    private readonly UpscaleTaskProcessor _upscaleProcessor;

    // Track recently deleted task IDs to prevent race condition with status updates
    // Using ConcurrentDictionary for thread-safe access from multiple processors
    // Value is deletion timestamp for cleanup
    private readonly ConcurrentDictionary<int, DateTime> _recentlyDeletedTaskIds = new();
    private readonly Timer _cleanupTimer;

    // Throttle delay for batching rapid task updates before re-sorting
    // Balance between responsiveness and performance
    private static readonly TimeSpan UpdateThrottleDelay = TimeSpan.FromMilliseconds(50);

    public TaskRegistry(
        IServiceScopeFactory scopeFactory,
        TaskQueue taskQueue,
        StandardTaskProcessor standardProcessor,
        UpscaleTaskProcessor upscalerProcessor,
        DistributedUpscaleTaskProcessor distributedUpscaleProcessor
    )
    {
        _scopeFactory = scopeFactory;
        _taskQueue = taskQueue;
        _standardProcessor = standardProcessor;
        _upscaleProcessor = upscalerProcessor;
        _distributedUpscaleProcessor = distributedUpscaleProcessor;

        // Setup periodic cleanup timer for deleted task IDs
        // Runs every 10 seconds to remove entries older than 5 seconds
        _cleanupTimer = new Timer(
            CleanupDeletedTaskIds,
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10)
        );

        // Standard view: non-upscale tasks, sorted by status priority, then Order, then CreatedAt
        // Use Throttle to batch rapid updates and reduce excessive re-sorting
        _tasks
            .Connect()
            .Filter(t =>
                t.Data
                    is not UpscaleTask
                        and not RenameUpscaledChaptersSeriesTask
                        and not RepairUpscaleTask
            )
            .Throttle(UpdateThrottleDelay)
            .SortAndBind(
                out ReadOnlyObservableCollection<PersistedTask> standard,
                SortExpressionComparer<PersistedTask>
                    .Ascending(x => x.GetStatusSortPriority())
                    .ThenByAscending(x => x.Order)
                    .ThenByAscending(x => x.CreatedAt)
            )
            .Subscribe(_ => { })
            .DisposeWith(_cleanups);
        StandardTasks = standard;

        // Upscale view: upscale tasks, sorted by status priority, then Order, then CreatedAt
        // Use Throttle to batch rapid updates and reduce excessive re-sorting
        _tasks
            .Connect()
            .Filter(t =>
                t.Data is UpscaleTask or RenameUpscaledChaptersSeriesTask or RepairUpscaleTask
            )
            .Throttle(UpdateThrottleDelay)
            .SortAndBind(
                out ReadOnlyObservableCollection<PersistedTask> upscale,
                SortExpressionComparer<PersistedTask>
                    .Ascending(x => x.GetStatusSortPriority())
                    .ThenByAscending(x => x.Order)
                    .ThenByAscending(x => x.CreatedAt)
            )
            .Subscribe(_ => { })
            .DisposeWith(_cleanups);
        UpscaleTasks = upscale;
    }

    public ReadOnlyObservableCollection<PersistedTask> StandardTasks { get; }
    public ReadOnlyObservableCollection<PersistedTask> UpscaleTasks { get; }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _cleanups.Dispose();
        _tasks.Dispose();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Initial load of all tasks (pending, processing, completed, failed, canceled)
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        List<PersistedTask> all = await db
            .PersistedTasks.AsNoTracking()
            .ToListAsync(cancellationToken);
        _tasks.AddOrUpdate(all);

        // Subscribe to queue and processor events to keep registry up to date
        _taskQueue.TaskEnqueuedOrChanged += OnTaskChanged;
        _taskQueue.TaskRemoved += OnTaskRemoved;

        _standardProcessor.StatusChanged += OnTaskChanged;
        _upscaleProcessor.StatusChanged += OnTaskChanged;
        _distributedUpscaleProcessor.StatusChanged += OnTaskChanged;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _taskQueue.TaskEnqueuedOrChanged -= OnTaskChanged;
        _taskQueue.TaskRemoved -= OnTaskRemoved;
        _standardProcessor.StatusChanged -= OnTaskChanged;
        _upscaleProcessor.StatusChanged -= OnTaskChanged;
        _distributedUpscaleProcessor.StatusChanged -= OnTaskChanged;
        return Task.CompletedTask;
    }

    private async Task OnTaskChanged(PersistedTask task)
    {
        // Check if task was recently deleted to prevent race condition
        if (_recentlyDeletedTaskIds.ContainsKey(task.Id))
        {
            // Task was deleted, ensure it's removed from cache
            _tasks.Remove(task.Id);
            return;
        }

        // Try to get existing cached task to update in-place
        var existingTask = _tasks.Lookup(task.Id);

        if (existingTask.HasValue)
        {
            // Re-check if task was deleted between initial check and now to prevent TOCTOU race
            if (_recentlyDeletedTaskIds.ContainsKey(task.Id))
            {
                // Task was deleted while we were processing, remove it from cache
                // This is necessary because the task still exists in cache but was just deleted from DB
                _tasks.Remove(task.Id);
                return;
            }

            // Update properties of existing cached task to preserve object identity
            // This avoids EF tracking collisions while maintaining reference stability
            var cached = existingTask.Value;
            cached.Status = task.Status;
            cached.ProcessedAt = task.ProcessedAt;
            cached.RetryCount = task.RetryCount;
            cached.Order = task.Order;
            cached.LastKeepAlive = task.LastKeepAlive;
            cached.Data = task.Data;

            // Notify DynamicData that this item was updated by re-adding it
            _tasks.AddOrUpdate(cached);
        }
        else
        {
            // First time seeing this task - verify it exists in database before caching
            // This handles the case where OnTaskChanged fires for a task after it was deleted
            // Note: This check is necessary because we can't distinguish between TaskEnqueuedOrChanged
            // events (always valid) and processor StatusChanged events (may fire after deletion)
            // Both use the same handler signature. Could optimize by using separate handlers if needed.
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var existsInDb = await dbContext.PersistedTasks.AnyAsync(t => t.Id == task.Id);

            if (!existsInDb)
            {
                // Task doesn't exist in database, don't add to cache
                return;
            }

            // Re-check if task was deleted after database query to prevent TOCTOU race
            if (_recentlyDeletedTaskIds.ContainsKey(task.Id))
            {
                // Task was deleted while we were checking database, don't add to cache
                return;
            }

            // First time seeing this task - create untracked copy to avoid EF collisions
            var untracked = new PersistedTask
            {
                Id = task.Id,
                Data = task.Data,
                Status = task.Status,
                CreatedAt = task.CreatedAt,
                RetryCount = task.RetryCount,
                ProcessedAt = task.ProcessedAt,
                Order = task.Order,
                LastKeepAlive = task.LastKeepAlive,
            };
            _tasks.AddOrUpdate(untracked);
        }
    }

    private Task OnTaskRemoved(PersistedTask task)
    {
        _tasks.Remove(task.Id);

        // Track as recently deleted to prevent race condition with status updates
        // Periodic cleanup timer will remove old entries
        _recentlyDeletedTaskIds.TryAdd(task.Id, DateTime.UtcNow);

        return Task.CompletedTask;
    }

    private void CleanupDeletedTaskIds(object? state)
    {
        try
        {
            // Remove entries older than 5 seconds
            var cutoff = DateTime.UtcNow.AddSeconds(-5);
            var keysToRemove = _recentlyDeletedTaskIds
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _recentlyDeletedTaskIds.TryRemove(key, out _);
            }
        }
        catch (Exception)
        {
            // Silently ignore exceptions to ensure timer continues functioning
            // Cleanup is not critical and will retry on next timer tick
        }
    }
}

internal static class DisposableExtensions
{
    public static void DisposeWith(this IDisposable disposable, CompositeDisposable cd)
    {
        cd.Add(disposable);
    }
}
