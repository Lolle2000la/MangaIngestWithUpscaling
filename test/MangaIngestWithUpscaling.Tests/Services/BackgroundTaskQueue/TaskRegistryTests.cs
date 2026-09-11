using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class TaskRegistryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_CompletedReturnsLowestPriority()
    {
        // Arrange
        var completedTask = new PersistedTask { Status = PersistedTaskStatus.Completed };

        // Act
        int priority = completedTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_CanceledReturnsLowestPriority()
    {
        // Arrange
        var canceledTask = new PersistedTask { Status = PersistedTaskStatus.Canceled };

        // Act
        int priority = canceledTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_ProcessingReturnsSecondPriority()
    {
        // Arrange
        var processingTask = new PersistedTask { Status = PersistedTaskStatus.Processing };

        // Act
        int priority = processingTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(1, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_PendingReturnsThirdPriority()
    {
        // Arrange
        var pendingTask = new PersistedTask { Status = PersistedTaskStatus.Pending };

        // Act
        int priority = pendingTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(2, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_FailedReturnsLowestPriority()
    {
        // Arrange
        var failedTask = new PersistedTask { Status = PersistedTaskStatus.Failed };

        // Act
        int priority = failedTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_SortOrderIsCorrect()
    {
        // Arrange
        var tasks = new List<PersistedTask>
        {
            new()
            {
                Id = 1,
                Status = PersistedTaskStatus.Failed,
                Order = 1,
            },
            new()
            {
                Id = 2,
                Status = PersistedTaskStatus.Pending,
                Order = 2,
            },
            new()
            {
                Id = 3,
                Status = PersistedTaskStatus.Processing,
                Order = 3,
            },
            new()
            {
                Id = 4,
                Status = PersistedTaskStatus.Completed,
                Order = 4,
            },
            new()
            {
                Id = 5,
                Status = PersistedTaskStatus.Canceled,
                Order = 5,
            },
        };

        // Act: Sort by status priority, then by order
        var sortedTasks = tasks
            .OrderBy(t => t.GetStatusSortPriority())
            .ThenBy(t => t.Order)
            .ToList();

        // Assert: Order should be Failed, Completed, Canceled, Processing, Pending
        Assert.Equal(1, sortedTasks[0].Id); // Failed
        Assert.Equal(4, sortedTasks[1].Id); // Completed
        Assert.Equal(5, sortedTasks[2].Id); // Canceled
        Assert.Equal(3, sortedTasks[3].Id); // Processing
        Assert.Equal(2, sortedTasks[4].Id); // Pending
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskRegistry_RemovesTasks_WhenQueueEmitsRemoval()
    {
        // Arrange: build minimal service provider and seed a task
        var services = new ServiceCollection();
        var dbName = $"TaskRegistry_{Guid.NewGuid()}";
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = true })
        );
        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        services.AddSingleton(Substitute.For<ITaskPersistenceService>());

        using ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var queueLogger = provider.GetRequiredService<ILogger<TaskQueue>>();
        var taskQueue = new TaskQueue(scopeFactory, queueLogger);

        var persistence = provider.GetRequiredService<ITaskPersistenceService>();
        var standard = new StandardTaskProcessor(
            taskQueue,
            scopeFactory,
            Substitute.For<ILogger<StandardTaskProcessor>>(),
            persistence
        );
        var upscaler = new UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            persistence,
            new PreprocessedInputCache()
        );
        var distributed = new DistributedUpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );

        var registry = new TaskRegistry(scopeFactory, taskQueue, standard, upscaler, distributed);
        await registry.StartAsync(TestContext.Current.CancellationToken);

        // Enqueue a task so the registry gets an entry via TaskEnqueuedOrChanged
        await taskQueue.EnqueueAsync(new LoggingTask { Message = "keep" });

        var snapshot = taskQueue.GetStandardSnapshot();
        Assert.Single(snapshot);
        var persistedId = snapshot[0].Id;

        // Allow event propagation into registry
        for (int i = 0; i < 5 && !registry.StandardTasks.Any(t => t.Id == persistedId); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Contains(registry.StandardTasks, t => t.Id == persistedId);

        // Act: remove the task via queue and wait for registry to update
        await taskQueue.RemoveTaskAsync(
            new PersistedTask
            {
                Id = persistedId,
                Data = new LoggingTask { Message = "keep" },
            }
        );

        for (int i = 0; i < 5 && registry.StandardTasks.Any(t => t.Id == persistedId); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain(registry.StandardTasks, t => t.Id == persistedId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskRegistry_SnapshotsAndChangeEvents_ReflectAdditionsAndRemovals()
    {
        // Arrange
        using ServiceProvider provider = BuildProvider($"TaskRegistrySnapshots_{Guid.NewGuid()}");
        var registry = BuildRegistry(provider, out var taskQueue);

        int standardChanges = 0;
        int upscaleChanges = 0;
        registry.StandardTasksChanged += () => Interlocked.Increment(ref standardChanges);
        registry.UpscaleTasksChanged += () => Interlocked.Increment(ref upscaleChanges);

        await registry.StartAsync(TestContext.Current.CancellationToken);

        // Act: enqueue one standard and one upscale task.
        await taskQueue.EnqueueAsync(new LoggingTask { Message = "standard" });
        await taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        await WaitUntilAsync(
            () =>
                registry.GetStandardSnapshot().Count == 1
                && registry.GetUpscaleSnapshot().Count == 1,
            TestContext.Current.CancellationToken
        );

        // Assert: snapshots are populated and the matching change events fired.
        Assert.Single(registry.GetStandardSnapshot());
        Assert.Single(registry.GetUpscaleSnapshot());
        Assert.True(standardChanges >= 1);
        Assert.True(upscaleChanges >= 1);

        // Act: batch remove both tasks.
        List<PersistedTask> all = registry
            .GetStandardSnapshot()
            .Concat(registry.GetUpscaleSnapshot())
            .ToList();
        await taskQueue.RemoveTasksAsync(all);

        await WaitUntilAsync(
            () =>
                registry.GetStandardSnapshot().Count == 0
                && registry.GetUpscaleSnapshot().Count == 0,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Empty(registry.GetStandardSnapshot());
        Assert.Empty(registry.GetUpscaleSnapshot());

        await registry.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskRegistry_LateUpdateAfterRemovalFlushed_DoesNotResurrectTask()
    {
        using ServiceProvider provider = BuildProvider($"TaskRegistryTombstone_{Guid.NewGuid()}");
        var registry = BuildRegistry(provider, out _);

        var task = new PersistedTask
        {
            Id = 4242,
            Data = new LoggingTask { Message = "ghost" },
            Status = PersistedTaskStatus.Pending,
        };

        // Seed the task and apply the update.
        await registry.OnTaskChanged(task);
        registry.FlushPendingUpdates();
        Assert.Single(registry.GetStandardSnapshot());

        // Remove it and apply the removal.
        await registry.OnTaskRemoved(task);
        registry.FlushPendingUpdates();
        Assert.Empty(registry.GetStandardSnapshot());

        // A status update that was already in flight when the removal was applied must not bring
        // the task back (removed ids are tombstoned).
        await registry.OnTaskChanged(task);
        registry.FlushPendingUpdates();

        Assert.Empty(registry.GetStandardSnapshot());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskRegistry_ConcurrentEnqueuesAndSnapshotReads_DoNotThrowOrHang()
    {
        // Regression guard: the flush loop, the enqueue path and UI snapshot reads all touch the
        // registry concurrently. A deadlock or thrown exception would fail this test.
        using ServiceProvider provider = BuildProvider($"TaskRegistryStress_{Guid.NewGuid()}");
        var registry = BuildRegistry(provider, out var taskQueue);
        await registry.StartAsync(TestContext.Current.CancellationToken);

        var errors = new ConcurrentBag<Exception>();

        var writers = Enumerable
            .Range(0, 3)
            .Select(worker =>
                Task.Run(async () =>
                {
                    try
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            await taskQueue.EnqueueAsync(
                                new LoggingTask { Message = $"{worker}-{i}" }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                })
            )
            .ToList();

        var readers = Enumerable
            .Range(0, 3)
            .Select(_ =>
                Task.Run(async () =>
                {
                    try
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _ = registry.GetStandardSnapshot().Count;
                            _ = registry.GetUpscaleSnapshot().Count;
                            if (i % 10 == 0)
                            {
                                await Task.Yield();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                })
            )
            .ToList();

        await Task.WhenAll(writers.Concat(readers))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            () => registry.GetStandardSnapshot().Count == 60,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(errors);
        Assert.Equal(60, registry.GetStandardSnapshot().Count);

        await registry.StopAsync(TestContext.Current.CancellationToken);
    }

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = true })
        );

        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        services.AddSingleton(Substitute.For<ITaskPersistenceService>());

        return services.BuildServiceProvider();
    }

    private static TaskRegistry BuildRegistry(ServiceProvider provider, out TaskQueue taskQueue)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        taskQueue = new TaskQueue(scopeFactory, provider.GetRequiredService<ILogger<TaskQueue>>());

        var persistence = provider.GetRequiredService<ITaskPersistenceService>();
        var standard = new StandardTaskProcessor(
            taskQueue,
            scopeFactory,
            Substitute.For<ILogger<StandardTaskProcessor>>(),
            persistence
        );
        var upscaler = new UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            persistence,
            new PreprocessedInputCache()
        );
        var distributed = new DistributedUpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );

        return new TaskRegistry(scopeFactory, taskQueue, standard, upscaler, distributed);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken
    )
    {
        for (int i = 0; i < 40 && !condition(); i++)
        {
            await Task.Delay(50, cancellationToken);
        }
    }
}
