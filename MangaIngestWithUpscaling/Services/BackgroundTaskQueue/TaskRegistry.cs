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
            .Throttle(TimeSpan.FromMilliseconds(100)) // Batch updates within 100ms window
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
            .Throttle(TimeSpan.FromMilliseconds(100)) // Batch updates within 100ms window
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

    private Task OnTaskChanged(PersistedTask task)
    {
        // Update in-place instead of cloning to preserve object identity
        // DynamicData will detect the change through the cache update
        _tasks.AddOrUpdate(task);
        return Task.CompletedTask;
    }

    private Task OnTaskRemoved(PersistedTask task)
    {
        _tasks.Remove(task.Id);
        return Task.CompletedTask;
    }
}

internal static class DisposableExtensions
{
    public static void DisposeWith(this IDisposable disposable, CompositeDisposable cd)
    {
        cd.Add(disposable);
    }
}
