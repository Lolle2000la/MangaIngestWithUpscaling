using System.Reflection;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class UpscaleTaskProcessorTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UpscaleTaskProcessor> _mockLogger;
    private readonly ITaskPersistenceService _mockPersistence;
    private readonly IOptions<UpscalerConfig> _mockOptions;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly TaskQueue _taskQueue;
    private readonly UpscaleTaskProcessor _processor;

    public UpscaleTaskProcessorTests()
    {
        var services = new ServiceCollection();
        var dbName = $"TestDb_Processor_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        _mockPersistence = Substitute.For<ITaskPersistenceService>();
        _mockOptions = Substitute.For<IOptions<UpscalerConfig>>();
        _mockOptions.Value.Returns(new UpscalerConfig { RemoteOnly = false });

        var mockQueueCleanup = Substitute.For<IQueueCleanup>();
        mockQueueCleanup
            .CleanupAsync()
            .Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));

        services.AddScoped<IQueueCleanup>(_ => mockQueueCleanup);
        services.AddSingleton(_mockPersistence);

        var serviceProvider = services.BuildServiceProvider();
        _serviceProvider = serviceProvider;
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Simulate production: claiming only succeeds if the task is Pending in the database.
        _mockPersistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async x =>
            {
                var taskId = (int)x[0];
                var task = await _dbContext.PersistedTasks.FindAsync(taskId);
                if (task == null)
                {
                    return false;
                }

                if (task.Status != PersistedTaskStatus.Pending)
                {
                    return false;
                }
                // Simulate claim by updating status
                task.Status = PersistedTaskStatus.Processing;
                await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
                return true;
            });

        _mockLogger = Substitute.For<ILogger<UpscaleTaskProcessor>>();
        var queueLogger = Substitute.For<ILogger<TaskQueue>>();

        _taskQueue = new TaskQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            queueLogger
        );

        _processor = new UpscaleTaskProcessor(
            _taskQueue,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            _mockLogger,
            _mockPersistence,
            new PreprocessedInputCache()
        );
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ShouldPrioritizeReroutedTasksOverSignals()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // 1. Enqueue a normal upscale task (will stay in main queue)
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });

        // 2. Enqueue another task that we will simulate as being "rerouted"
        await _taskQueue.EnqueueAsync(
            new RepairUpscaleTask { ChapterId = 1, UpscalerProfileId = 1 }
        );

        // Get the tasks from the DB to make them "real"
        var tasks = await _dbContext
            .PersistedTasks.OrderBy(t => t.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var upscaleTask = tasks[0];
        var reroutedTask = tasks[1];

        // Simulate DistributedUpscaleTaskProcessor behavior:
        // Mark the task as Processing (already claimed) before sending it to the local processor.
        reroutedTask.Status = PersistedTaskStatus.Processing;
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        // 3. Send rerouted task directly to the local channel
        await _taskQueue.SendToLocalUpscaleAsync(reroutedTask, cts.Token);

        PersistedTask? firstProcessed = null;
        var tcs = new TaskCompletionSource();

        _processor.StatusChanged += (task) =>
        {
            if (task.Status == PersistedTaskStatus.Processing && firstProcessed == null)
            {
                firstProcessed = task;
                tcs.TrySetResult();
            }
            return Task.CompletedTask;
        };

        // Act
        var runTask = _processor.StartAsync(cts.Token);

        // Wait for first task to be processed
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await runTask;

        // Assert
        Assert.NotNull(firstProcessed);
        Assert.Equal(reroutedTask.Id, firstProcessed.Id); // Rerouted should come first regardless of order
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ShouldRespectPriorityChangeAfterSignal()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // 1. Enqueue two tasks
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 2, UpscalerProfileId = 1 });

        // 2. Get tasks from snapshot and reorder
        var tasks = _taskQueue.GetUpscaleSnapshot();
        var task1 = tasks.First(t => ((UpscaleTask)t.Data).ChapterId == 1);
        var task2 = tasks.First(t => ((UpscaleTask)t.Data).ChapterId == 2);

        // Make task 2 higher priority
        await _taskQueue.ReorderTaskAsync(task2, 0);

        var processedTasks = new List<PersistedTask>();
        var tcs = new TaskCompletionSource();

        _processor.StatusChanged += (task) =>
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                processedTasks.Add(task);
                if (processedTasks.Count == 2)
                {
                    tcs.TrySetResult();
                }
            }
            return Task.CompletedTask;
        };

        // 3. Start processor
        var runTask = _processor.StartAsync(cts.Token);

        // 4. Wait for processing to complete
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await runTask;

        // Assert
        Assert.Equal(2, processedTasks.Count);
        Assert.Equal(2, ((UpscaleTask)processedTasks[0].Data).ChapterId);
        Assert.Equal(1, ((UpscaleTask)processedTasks[1].Data).ChapterId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_WhenClaimThrows_ReenqueuesAndProcessesTheSameTask()
    {
        // Regression guard: a transient claim failure used to drop the dequeued task, leaving its
        // still-Pending row invisible until the 10-minute replay. It must be re-enqueued and
        // retried instead.
        bool firstClaim = true;
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (firstClaim)
                {
                    firstClaim = false;
                    throw new InvalidOperationException("claim failed");
                }

                return Task.FromResult(true);
            });

        var processor = new UpscaleTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            _mockLogger,
            persistence,
            new PreprocessedInputCache()
        );

        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 2, UpscalerProfileId = 1 });

        var processed = new TaskCompletionSource<PersistedTask>();
        processor.StatusChanged += task =>
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                processed.TrySetResult(task);
            }
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        PersistedTask processedTask = await processed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, ((UpscaleTask)processedTask.Data).ChapterId);

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_WhenClaimIsCancelled_PersistsCancelInsteadOfDroppingIt()
    {
        // Regression guard: CancelCurrent cancelling the claim used to be swallowed, so the
        // intended cancel was never persisted.
        var persistence = Substitute.For<ITaskPersistenceService>();
        int claimCalls = 0;
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                claimCalls++;
                if (claimCalls == 1)
                {
                    throw new OperationCanceledException("canceled");
                }

                return Task.FromResult(true);
            });

        var processor = new UpscaleTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            _mockLogger,
            persistence,
            new PreprocessedInputCache()
        );

        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });

        var canceled = new TaskCompletionSource<PersistedTask>();
        processor.StatusChanged += task =>
        {
            if (task.Status == PersistedTaskStatus.Canceled)
            {
                canceled.TrySetResult(task);
            }
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        PersistedTask canceledTask = await canceled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, ((UpscaleTask)canceledTask.Data).ChapterId);
        await persistence
            .Received(1)
            .CancelTaskAsync(Arg.Any<int>(), false, Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    private static bool IsExecuteTaskFaulted(BackgroundService service)
    {
        Task? executeTask = (Task?)
            typeof(BackgroundService)
                .GetField("_executeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service);
        return executeTask?.IsFaulted ?? false;
    }
}
