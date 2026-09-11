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
    private readonly PendingTaskChanges _pending = new();
    private readonly CancellationTokenSource _cts = new();

    // A single immutable holder so readers always observe the standard and upscale snapshots from
    // the same generation. Swapped atomically once per rebuild; never null.
    private volatile Snapshots _snapshots = new(
        Array.Empty<PersistedTask>(),
        Array.Empty<PersistedTask>()
    );
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
    public IReadOnlyList<PersistedTask> GetStandardSnapshot() => _snapshots.Standard;

    /// <summary>
    ///     Returns a consistent, sorted snapshot of the upscale tasks. The returned array is
    ///     replaced atomically on each flush, so callers can safely enumerate it from any thread.
    /// </summary>
    public IReadOnlyList<PersistedTask> GetUpscaleSnapshot() => _snapshots.Upscale;

    public void Dispose()
    {
        // The instance is registered both as a plain singleton (TaskRegistry) and as an
        // IHostedService, so the DI container disposes it once per registration. Make the
        // teardown idempotent to avoid calling Cancel() on an already-disposed CTS.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Unsubscribe here as well so disposal without a prior StopAsync cannot leave the queue
        // and processors holding references to this registry. Re-unsubscribing after StopAsync is
        // safe.
        _taskQueue.TaskEnqueuedOrChanged -= OnTaskChanged;
        _taskQueue.TaskRemoved -= OnTaskRemoved;
        _standardProcessor.StatusChanged -= OnTaskChanged;
        _upscaleProcessor.StatusChanged -= OnTaskChanged;
        _distributedUpscaleProcessor.StatusChanged -= OnTaskChanged;

        _cts.Cancel();
        _cts.Dispose();
        _cleanups.Dispose();
        _tasks.Dispose();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe before loading the DB snapshot: an event handler only buffers into _pending
        // (no DynamicData mutation), so subscribing first prevents a status transition that fires
        // between the snapshot query and the subscription from being lost. The flush loop, which is
        // the only thing that mutates _tasks, starts after the load.
        _taskQueue.TaskEnqueuedOrChanged += OnTaskChanged;
        _taskQueue.TaskRemoved += OnTaskRemoved;

        _standardProcessor.StatusChanged += OnTaskChanged;
        _upscaleProcessor.StatusChanged += OnTaskChanged;
        _distributedUpscaleProcessor.StatusChanged += OnTaskChanged;

        // Initial load of all tasks (pending, processing, completed, failed, canceled)
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        List<PersistedTask> all = await db
            .PersistedTasks.AsNoTracking()
            .ToListAsync(cancellationToken);
        _tasks.AddOrUpdate(all);
        RebuildSnapshots();

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

    internal Task OnTaskChanged(PersistedTask task)
    {
        _pending.QueueUpdate(CloneShallow(task));
        return Task.CompletedTask;
    }

    internal Task OnTaskRemoved(PersistedTask task)
    {
        _pending.QueueRemoval(CloneShallow(task));
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

    internal void FlushPendingUpdates()
    {
        if (_pending.IsEmpty)
            return;

        _pending.Drain(
            out List<PersistedTask> itemsToUpdate,
            out List<PersistedTask> itemsToRemove
        );

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
    ///     swaps the holder atomically, so readers never observe a partially-updated view and can
    ///     never see the standard and upscale views from different generations.
    /// </summary>
    private void RebuildSnapshots()
    {
        _snapshots = new Snapshots(StandardTasks.ToArray(), UpscaleTasks.ToArray());
    }

    private sealed record Snapshots(
        IReadOnlyList<PersistedTask> Standard,
        IReadOnlyList<PersistedTask> Upscale
    );

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
