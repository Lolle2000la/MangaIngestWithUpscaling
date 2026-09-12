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

        _mockPersistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(1);

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
    public async Task ExecuteAsync_WhenClaimThrows_RecoversFailedTaskAndProcessesTheNext()
    {
        // Regression guard: a claim failure must not fault ExecuteAsync or be silently dropped.
        // The failed task is reconciled to Pending and retried promptly, so it is processed before
        // the next task rather than waiting for the periodic replayer.
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
        persistence
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        var processor = new FastRetryUpscaleTaskProcessor(
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
        await persistence
            .Received(1)
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_WhenClaimFailsPersistently_DoesNotHotLoopAndKeepsServingOtherTasks()
    {
        // Regression guard: a persistent claim failure must not spin on the same task and starve
        // later tasks. It is retried a bounded number of times and the later task still runs.
        var persistence = Substitute.For<ITaskPersistenceService>();
        int failedClaimCalls = 0;
        int failedTaskId;

        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        failedTaskId = _taskQueue.GetUpscaleSnapshot().Single().Id;
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 2, UpscalerProfileId = 1 });

        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if ((int)ci[0] == failedTaskId)
                {
                    Interlocked.Increment(ref failedClaimCalls);
                    throw new InvalidOperationException("persistent claim failure");
                }

                return Task.FromResult(true);
            });
        persistence
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        var processor = new FastRetryUpscaleTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            _mockLogger,
            persistence,
            new PreprocessedInputCache()
        );

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

        Assert.Equal(2, ((UpscaleTask)processedTask.Data).ChapterId);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(4, Volatile.Read(ref failedClaimCalls));

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_WhenCompleteAffectsNoRows_DoesNotMarkCompletedInMemory()
    {
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        persistence.CompleteTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);

        var processor = CreateExposedProcessor(persistence);
        var task = new PersistedTask
        {
            Id = 601,
            Data = new NoOpTask(),
            Status = PersistedTaskStatus.Pending,
        };

        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        Assert.NotEqual(PersistedTaskStatus.Completed, task.Status);
        Assert.Null(task.ProcessedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_WhenFailAffectsNoRows_DoesNotMarkFailedOrBumpRetryInMemory()
    {
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        persistence.FailTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);

        var processor = CreateExposedProcessor(persistence);
        var task = new PersistedTask
        {
            Id = 602,
            Data = new ThrowingTask(),
            Status = PersistedTaskStatus.Pending,
            RetryCount = 3,
        };

        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        Assert.NotEqual(PersistedTaskStatus.Failed, task.Status);
        Assert.Equal(3, task.RetryCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_WhenCancelAffectsNoRows_DoesNotMarkCanceledInMemory()
    {
        // Regression guard: the cancel path mirrored Canceled in memory even when the guarded write
        // affected no row (the row was already terminal), telling the UI a contradictory status.
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        persistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var processor = CreateExposedProcessor(persistence);
        var task = new PersistedTask
        {
            Id = 603,
            Data = new CancelledTask(),
            Status = PersistedTaskStatus.Pending,
        };

        var emitted = new List<PersistedTaskStatus>();
        processor.StatusChanged += t =>
        {
            emitted.Add(t.Status);
            return Task.CompletedTask;
        };

        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        Assert.NotEqual(PersistedTaskStatus.Canceled, task.Status);
        Assert.DoesNotContain(PersistedTaskStatus.Canceled, emitted);
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
        persistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(1);

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

    private ExposedUpscaleTaskProcessor CreateExposedProcessor(
        ITaskPersistenceService persistence
    ) =>
        new(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            _mockLogger,
            persistence,
            new PreprocessedInputCache()
        );

    private static bool IsExecuteTaskFaulted(BackgroundService service)
    {
        Task? executeTask = (Task?)
            typeof(BackgroundService)
                .GetField("_executeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service);
        return executeTask?.IsFaulted ?? false;
    }

    private sealed class ExposedUpscaleTaskProcessor(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> upscalerConfig,
        ILogger<UpscaleTaskProcessor> logger,
        ITaskPersistenceService taskPersistenceService,
        IPreprocessedInputCache preprocessedCache
    )
        : UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            upscalerConfig,
            logger,
            taskPersistenceService,
            preprocessedCache
        )
    {
        public Task InvokeProcessTaskAsync(
            PersistedTask task,
            CancellationToken cancellationToken
        ) => ProcessTaskAsync(task, cancellationToken);
    }

    /// <summary>
    ///     Processor with a tiny, deterministic claim-retry backoff so bounded-retry tests run fast.
    /// </summary>
    private sealed class FastRetryUpscaleTaskProcessor(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> upscalerConfig,
        ILogger<UpscaleTaskProcessor> logger,
        ITaskPersistenceService taskPersistenceService,
        IPreprocessedInputCache preprocessedCache
    )
        : UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            upscalerConfig,
            logger,
            taskPersistenceService,
            preprocessedCache
        )
    {
        private static readonly IReadOnlyList<TimeSpan> TinyBackoff = new[]
        {
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
        };

        protected override IReadOnlyList<TimeSpan> ClaimRetryBackoff => TinyBackoff;
    }

    private sealed class NoOpTask : BaseTask
    {
        public override Task ProcessAsync(
            IServiceProvider services,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class ThrowingTask : BaseTask
    {
        public override Task ProcessAsync(
            IServiceProvider services,
            CancellationToken cancellationToken
        ) => Task.FromException(new InvalidOperationException("boom"));
    }

    private sealed class CancelledTask : BaseTask
    {
        public override Task ProcessAsync(
            IServiceProvider services,
            CancellationToken cancellationToken
        ) => Task.FromException(new OperationCanceledException("canceled"));
    }
}
