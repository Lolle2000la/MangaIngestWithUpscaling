using System.Reflection;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class StandardTaskProcessorTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<StandardTaskProcessor> _mockLogger;
    private readonly ITaskPersistenceService _mockPersistence;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly TaskQueue _taskQueue;
    private readonly StandardTaskProcessor _processor;

    public StandardTaskProcessorTests()
    {
        var services = new ServiceCollection();
        var dbName = $"TestDb_StandardProcessor_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        _mockPersistence = Substitute.For<ITaskPersistenceService>();
        _mockPersistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

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

        _mockLogger = Substitute.For<ILogger<StandardTaskProcessor>>();
        var queueLogger = Substitute.For<ILogger<TaskQueue>>();

        _taskQueue = new TaskQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            queueLogger
        );

        _processor = new StandardTaskProcessor(
            _taskQueue,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger,
            _mockPersistence
        );
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ShouldRespectPriorityChangeAfterSignal()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // 1. Enqueue two tasks
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "low" });
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "high" });

        // 2. Get the tasks from the snapshot and reorder them
        var tasks = _taskQueue.GetStandardSnapshot();
        var lowTask = tasks.First(t => ((LoggingTask)t.Data).Message == "low");
        var highTask = tasks.First(t => ((LoggingTask)t.Data).Message == "high");

        // Make "high" priority 0 (higher than "low" which is likely 1)
        await _taskQueue.ReorderTaskAsync(highTask, 0);

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
        Assert.Equal("high", ((LoggingTask)processedTasks[0].Data).Message);
        Assert.Equal("low", ((LoggingTask)processedTasks[1].Data).Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public Task ExecuteAsync_WhenClaimIsCancelled_PersistsCancelAndKeepsProcessingSubsequentTasks() =>
        RunClaimFailureScenarioAsync(
            new OperationCanceledException("canceled"),
            expectedFirstProcessed: "second"
        );

    [Fact]
    [Trait("Category", "Unit")]
    public Task ExecuteAsync_WhenClaimThrows_RecoversFailedTaskAndKeepsProcessingSubsequentTasks() =>
        RunClaimFailureScenarioAsync(
            new InvalidOperationException("claim failed"),
            expectedFirstProcessed: "second"
        );

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_WhenClaimFailsPersistently_DoesNotHotLoopAndKeepsServingOtherTasks()
    {
        // Regression guard: a persistent claim failure used to re-enqueue the same task at the
        // head of the sorted set, spinning unboundedly and starving every later task. The failed
        // task must be attempted a bounded number of times, left recoverable, and the innocent
        // task behind it must still run.
        var persistence = Substitute.For<ITaskPersistenceService>();
        int failedClaimCalls = 0;
        int failedTaskId;

        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "poison" });
        failedTaskId = _taskQueue.GetStandardSnapshot().Single().Id;
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "innocent" });

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

        var processor = new StandardTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger,
            persistence
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

        Assert.Equal("innocent", ((LoggingTask)processedTask.Data).Message);

        // Give a hot loop time to reveal itself, then assert the attempts are bounded.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref failedClaimCalls));

        // The poisoned task was handed back to Pending for the periodic replayer, not lost.
        await persistence
            .Received(1)
            .RequeueStrandedTaskAsync(failedTaskId, Arg.Any<CancellationToken>());

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db
                .PersistedTasks.AsNoTracking()
                .FirstAsync(t => t.Id == failedTaskId, TestContext.Current.CancellationToken);
            Assert.Equal(PersistedTaskStatus.Pending, row.Status);
        }

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_WhenCompleteAffectsNoRows_DoesNotMarkCompletedInMemory()
    {
        // Regression guard: the local processor used to ignore the guarded CompleteTaskAsync row
        // count and always mark the task Completed in memory/UI even when the row was concurrently
        // canceled or removed.
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        persistence.CompleteTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);

        var processor = CreateExposedProcessor(persistence);
        var task = new PersistedTask
        {
            Id = 501,
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
        // Regression guard: a late failure on an already-terminal row must not emit a contradictory
        // Failed status or inflate the in-memory retry count.
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        persistence.FailTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);

        var processor = CreateExposedProcessor(persistence);
        var task = new PersistedTask
        {
            Id = 502,
            Data = new ThrowingTask(),
            Status = PersistedTaskStatus.Pending,
            RetryCount = 2,
        };

        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        Assert.NotEqual(PersistedTaskStatus.Failed, task.Status);
        Assert.Equal(2, task.RetryCount);
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

        var processor = new StandardTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger,
            persistence
        );

        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "cancel-me" });

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

        Assert.Equal("cancel-me", ((LoggingTask)canceledTask.Data).Message);
        await persistence
            .Received(1)
            .CancelTaskAsync(Arg.Any<int>(), false, Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    private async Task RunClaimFailureScenarioAsync(
        Exception claimFailure,
        string expectedFirstProcessed
    )
    {
        // Regression guard: a claim failure used to escape ProcessTaskAsync and fault ExecuteAsync,
        // which under BackgroundServiceExceptionBehavior.StopHost stops the whole application.
        bool firstClaim = true;
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (firstClaim)
                {
                    firstClaim = false;
                    throw claimFailure;
                }

                return Task.FromResult(true);
            });

        var processor = new StandardTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger,
            persistence
        );

        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "first" });
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "second" });

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

        // The first claim failed: a thrown exception reconciles the row to Pending and drops the
        // in-memory copy (so the second is processed first), while a cancellation persists the
        // cancel and drops it (so the second is first as well).
        Assert.Equal(expectedFirstProcessed, ((LoggingTask)processedTask.Data).Message);

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    private ExposedStandardTaskProcessor CreateExposedProcessor(
        ITaskPersistenceService persistence
    ) =>
        new(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger,
            persistence
        );

    private static bool IsExecuteTaskFaulted(BackgroundService service)
    {
        Task? executeTask = (Task?)
            typeof(BackgroundService)
                .GetField("_executeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service);
        return executeTask?.IsFaulted ?? false;
    }

    private sealed class ExposedStandardTaskProcessor(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<StandardTaskProcessor> logger,
        ITaskPersistenceService taskPersistenceService
    ) : StandardTaskProcessor(taskQueue, scopeFactory, logger, taskPersistenceService)
    {
        public Task InvokeProcessTaskAsync(
            PersistedTask task,
            CancellationToken cancellationToken
        ) => ProcessTaskAsync(task, cancellationToken);
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
}
