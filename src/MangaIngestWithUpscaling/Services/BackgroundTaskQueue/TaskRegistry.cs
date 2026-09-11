using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using AutoRegisterInject;
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
    private readonly ILogger<TaskRegistry>? _logger;
    private readonly ConcurrentDictionary<int, PersistedTask> _pendingUpdates = new();
    private readonly ConcurrentDictionary<int, PersistedTask> _pendingRemovals = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile PersistedTask[] _standardSnapshot = [];
    private volatile PersistedTask[] _upscaleSnapshot = [];
    private Task? _flushTask;
    private int _disposed;

    public TaskRegistry(
        IServiceScopeFactory scopeFactory,
        TaskQueue taskQueue,
        StandardTaskProcessor standardProcessor,
        UpscaleTaskProcessor upscalerProcessor,
        DistributedUpscaleTaskProcessor distributedUpscaleProcessor,
        ILogger<TaskRegistry>? logger = null
    )
    {
        _scopeFactory = scopeFactory;
        _taskQueue = taskQueue;
        _standardProcessor = standardProcessor;
        _upscaleProcessor = upscalerProcessor;
        _distributedUpscaleProcessor = distributedUpscaleProcessor;
        _logger = logger;

        // Standard view: non-upscale tasks, sorted by status priority, then Order, then CreatedAt
        _tasks
            .Connect()
            .Filter(t => !TaskQueue.IsUpscaleTask(t.Data))
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
        _tasks
            .Connect()
            .Filter(t => TaskQueue.IsUpscaleTask(t.Data))
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

    /// <summary>
    ///     Raised once per flush batch after the standard-task view changed. Handlers run on the
    ///     flushing thread, so UI consumers must marshal to their own synchronization context.
    /// </summary>
    public event Action? StandardTasksChanged;

    /// <summary>
    ///     Raised once per flush batch after the upscale-task view changed. Handlers run on the
    ///     flushing thread, so UI consumers must marshal to their own synchronization context.
    /// </summary>
    public event Action? UpscaleTasksChanged;

    /// <summary>
    ///     Returns a consistent, sorted snapshot of the standard tasks. The returned array is
    ///     replaced atomically on each flush, so callers can safely enumerate it from any thread.
    /// </summary>
    public IReadOnlyList<PersistedTask> GetStandardSnapshot() => _standardSnapshot;

    /// <summary>
    ///     Returns a consistent, sorted snapshot of the upscale tasks. The returned array is
    ///     replaced atomically on each flush, so callers can safely enumerate it from any thread.
    /// </summary>
    public IReadOnlyList<PersistedTask> GetUpscaleSnapshot() => _upscaleSnapshot;

    public void Dispose()
    {
        // The instance is registered both as a plain singleton (TaskRegistry) and as an
        // IHostedService, so the DI container disposes it once per registration. Make the
        // teardown idempotent to avoid calling Cancel() on an already-disposed CTS.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _cts.Dispose();
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
        RebuildSnapshots();

        // Subscribe to queue and processor events to keep registry up to date
        _taskQueue.TaskEnqueuedOrChanged += OnTaskChanged;
        _taskQueue.TaskRemoved += OnTaskRemoved;

        _standardProcessor.StatusChanged += OnTaskChanged;
        _upscaleProcessor.StatusChanged += OnTaskChanged;
        _distributedUpscaleProcessor.StatusChanged += OnTaskChanged;

        _flushTask = RunFlushLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _taskQueue.TaskEnqueuedOrChanged -= OnTaskChanged;
        _taskQueue.TaskRemoved -= OnTaskRemoved;
        _standardProcessor.StatusChanged -= OnTaskChanged;
        _upscaleProcessor.StatusChanged -= OnTaskChanged;
        _distributedUpscaleProcessor.StatusChanged -= OnTaskChanged;

        await _cts.CancelAsync();
        if (_flushTask != null)
        {
            try
            {
                await _flushTask;
            }
            catch (OperationCanceledException) { }
        }

        FlushPendingUpdates();
    }

    private Task OnTaskChanged(PersistedTask task)
    {
        // A task already queued for removal must not be re-added by a stale update.
        if (_pendingRemovals.ContainsKey(task.Id))
            return Task.CompletedTask;

        _pendingUpdates[task.Id] = CloneShallow(task);
        return Task.CompletedTask;
    }

    private Task OnTaskRemoved(PersistedTask task)
    {
        _pendingUpdates.TryRemove(task.Id, out _);
        _pendingRemovals[task.Id] = CloneShallow(task);
        return Task.CompletedTask;
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                FlushPendingUpdates();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Error occurred while flushing pending task updates in TaskRegistry"
                );
            }
        }
    }

    private void FlushPendingUpdates()
    {
        if (_pendingUpdates.IsEmpty && _pendingRemovals.IsEmpty)
            return;

        var itemsToUpdate = new List<PersistedTask>();
        foreach (var key in _pendingUpdates.Keys.ToList())
        {
            if (_pendingUpdates.TryRemove(key, out var item))
            {
                itemsToUpdate.Add(item);
            }
        }

        var itemsToRemove = new List<PersistedTask>();
        foreach (var key in _pendingRemovals.Keys.ToList())
        {
            if (_pendingRemovals.TryRemove(key, out var item))
            {
                itemsToRemove.Add(item);
            }
        }

        if (itemsToUpdate.Count == 0 && itemsToRemove.Count == 0)
            return;

        bool standardChanged = false;
        bool upscaleChanged = false;
        foreach (var item in itemsToRemove.Concat(itemsToUpdate))
        {
            if (item.Data is null)
            {
                // The caller only provided an id (e.g. queue cleanup), so we cannot tell which
                // view it belonged to. Refresh both to stay safe.
                standardChanged = true;
                upscaleChanged = true;
            }
            else if (TaskQueue.IsUpscaleTask(item.Data))
            {
                upscaleChanged = true;
            }
            else
            {
                standardChanged = true;
            }
        }

        _tasks.Edit(updater =>
        {
            foreach (var item in itemsToRemove)
            {
                updater.Remove(item.Id);
            }

            if (itemsToUpdate.Count > 0)
            {
                updater.AddOrUpdate(itemsToUpdate);
            }
        });

        RebuildSnapshots();

        if (standardChanged)
            StandardTasksChanged?.Invoke();
        if (upscaleChanged)
            UpscaleTasksChanged?.Invoke();
    }

    /// <summary>
    ///     Rebuilds the immutable snapshots consumed by the UI. Runs on the flush thread and
    ///     swaps the arrays atomically, so readers never observe a partially-updated view.
    /// </summary>
    private void RebuildSnapshots()
    {
        _standardSnapshot = StandardTasks.ToArray();
        _upscaleSnapshot = UpscaleTasks.ToArray();
    }

    private static PersistedTask CloneShallow(PersistedTask src)
    {
        // Shallow copy to avoid EF tracking collisions from different DbContexts
        return new PersistedTask
        {
            Id = src.Id,
            Data = src.Data,
            Status = src.Status,
            CreatedAt = src.CreatedAt,
            RetryCount = src.RetryCount,
            ProcessedAt = src.ProcessedAt,
            Order = src.Order,
            LastKeepAlive = src.LastKeepAlive,
        };
    }
}

internal static class DisposableExtensions
{
    public static void DisposeWith(this IDisposable disposable, CompositeDisposable cd)
    {
        cd.Add(disposable);
    }
}
