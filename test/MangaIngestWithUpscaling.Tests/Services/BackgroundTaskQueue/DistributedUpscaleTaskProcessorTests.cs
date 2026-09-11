using System.Reflection;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class DistributedUpscaleTaskProcessorTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITaskPersistenceService _mockPersistence;
    private readonly IOptions<UpscalerConfig> _mockOptions;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly TaskQueue _taskQueue;
    private readonly DistributedUpscaleTaskProcessor _processor;

    public DistributedUpscaleTaskProcessorTests()
    {
        var services = new ServiceCollection();
        var dbName = $"TestDb_DistributedProcessor_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        _mockPersistence = Substitute.For<ITaskPersistenceService>();
        _mockPersistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _mockPersistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(1);

        _mockOptions = Substitute.For<IOptions<UpscalerConfig>>();
        _mockOptions.Value.Returns(new UpscalerConfig { RemoteOnly = true });

        var mockQueueCleanup = Substitute.For<IQueueCleanup>();
        mockQueueCleanup
            .CleanupAsync()
            .Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => mockQueueCleanup);

        var mockLogger = Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>();
        services.AddSingleton(mockLogger);
        services.AddSingleton(_mockPersistence);

        var serviceProvider = services.BuildServiceProvider();
        _serviceProvider = serviceProvider;
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var queueLogger = Substitute.For<ILogger<TaskQueue>>();

        _taskQueue = new TaskQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            queueLogger
        );

        _processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            mockLogger,
            _mockPersistence
        );
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_ShouldRespectPriorityChangeAfterSignal()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // 1. Enqueue two tasks
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(2, 1));

        // 2. Get tasks from snapshot and reorder
        var tasks = _taskQueue.GetUpscaleSnapshot();
        var task1 = tasks.First(t => ((DetectSplitCandidatesTask)t.Data).ChapterId == 1);
        var task2 = tasks.First(t => ((DetectSplitCandidatesTask)t.Data).ChapterId == 2);

        // Make task 2 higher priority
        await _taskQueue.ReorderTaskAsync(task2, 0);

        // Act
        var runTask = _processor.StartAsync(cts.Token);

        // First worker request should get high priority task (task 2)
        var result1 = await _processor.GetTask(cts.Token);
        // Second worker request should get low priority task (task 1)
        var result2 = await _processor.GetTask(cts.Token);

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(2, ((DetectSplitCandidatesTask)result1.Data).ChapterId);
        Assert.Equal(1, ((DetectSplitCandidatesTask)result2.Data).ChapterId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsRunningRemotely_ShouldReturnTrueWhileProcessingRemotely_AndFalseWhenCompleted()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        var runTask = _processor.StartAsync(cts.Token);

        // Before GetTask, task is not running remotely
        Assert.False(_processor.IsRunningRemotely(1));

        // Act
        var task = await _processor.GetTask(cts.Token);

        // Assert while processing remotely
        Assert.NotNull(task);
        Assert.True(_processor.IsRunningRemotely(task.Id));

        // Complete task
        await _processor.TaskCompleted(task.Id);

        // Assert after completion
        Assert.False(_processor.IsRunningRemotely(task.Id));

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelCurrent_ShouldCancelRunningRemoteTask_AndRemoveFromRunningTasks()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        var runTask = _processor.StartAsync(cts.Token);
        var task = await _processor.GetTask(cts.Token);

        Assert.NotNull(task);
        Assert.True(_processor.IsRunningRemotely(task.Id));

        // Act
        await _processor.CancelCurrent(task);

        // Assert
        Assert.False(_processor.IsRunningRemotely(task.Id));
        Assert.Equal(PersistedTaskStatus.Canceled, task.Status);

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelCurrent_WhenTaskRemovedWhileCancelInFlight_StillEmitsCanceledStatus()
    {
        // Regression guard: TaskCompleted can drop the task from runningTasks while the guarded
        // cancel is in flight. The cancel is still the write that took effect, so the UI must be
        // told about Canceled instead of being left stuck on Processing forever.
        var task = new PersistedTask
        {
            Id = 9101,
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = PersistedTaskStatus.Processing,
        };

        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[task.Id] = task;

        _mockPersistence
            .CancelTaskAsync(task.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Simulate the concurrent TaskCompleted removing the task after the guarded cancel
                // committed but before CancelCurrent re-acquires _lock.
                runningTasks.Remove(task.Id);
                return 1;
            });

        var emitted = new List<PersistedTaskStatus>();
        _processor.StatusChanged += emittedTask =>
        {
            lock (emitted)
            {
                emitted.Add(emittedTask.Status);
            }

            return Task.CompletedTask;
        };

        await _processor.CancelCurrent(task);

        Assert.Equal(new[] { PersistedTaskStatus.Canceled }, emitted);
        Assert.False(_processor.IsRunningRemotely(task.Id));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelCurrent_WhenCancelAffectsNoRowAndTaskRemovedConcurrently_EmitsNothing()
    {
        // The guarded cancel lost the race to a terminal state, so even though the task is no
        // longer tracked, CancelCurrent must not raise a contradictory Canceled status.
        var task = new PersistedTask
        {
            Id = 9102,
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = PersistedTaskStatus.Processing,
        };

        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[task.Id] = task;

        _mockPersistence
            .CancelTaskAsync(task.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                runningTasks.Remove(task.Id);
                return 0;
            });

        var emitted = new List<PersistedTaskStatus>();
        _processor.StatusChanged += emittedTask =>
        {
            lock (emitted)
            {
                emitted.Add(emittedTask.Status);
            }

            return Task.CompletedTask;
        };

        await _processor.CancelCurrent(task);

        Assert.Empty(emitted);
        Assert.False(_processor.IsRunningRemotely(task.Id));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTaskAsync_ShouldCancelRunningRemoteTask()
    {
        // Removal is the stop signal for in-flight remote work: a removed task must not keep
        // running just because the removal happened through its row's queue.
        var cts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        var runTask = _processor.StartAsync(cts.Token);
        var task = await _processor.GetTask(cts.Token);

        Assert.NotNull(task);
        Assert.True(_processor.IsRunningRemotely(task.Id));

        // Act
        await _taskQueue.RemoveTaskAsync(task);

        // Assert: removal stops tracking without writing a status for the already-deleted row.
        Assert.False(_processor.IsRunningRemotely(task.Id));
        await _mockPersistence
            .DidNotReceive()
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForgetTask_DoesNotHoldLockWhileDisposingRepairContext()
    {
        // Regression guard: CleanupRepairFiles used to dispose the RepairContext (a recursive
        // directory delete) while holding _lock, blocking every other lock user for the duration.
        var disposeStarted = new ManualResetEventSlim(false);
        var releaseDispose = new ManualResetEventSlim(false);
        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            $"repair-context-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(workDirectory);
        var repairContext = new BlockingRepairContext(disposeStarted, releaseDispose)
        {
            WorkDirectory = workDirectory,
        };

        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[1] = new PersistedTask { Id = 1, Data = new RepairUpscaleTask() };
        runningTasks[2] = new PersistedTask { Id = 2, Data = new DetectSplitCandidatesTask(1, 1) };
        Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState> repairStates =
            GetPrivateField<Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState>>(
                _processor,
                "remoteRepairStates"
            );
        repairStates[1] = new DistributedUpscaleTaskProcessor.RemoteRepairState
        {
            RepairContext = repairContext,
        };

        Task forget = Task.Run(
            () => _processor.ForgetTask(1),
            TestContext.Current.CancellationToken
        );
        Assert.True(
            disposeStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "RepairContext.Dispose should have started."
        );

        // While dispose is blocked, an unrelated lock user must still make progress.
        Task<bool> check = Task.Run(
            () => _processor.IsRunningRemotely(2),
            TestContext.Current.CancellationToken
        );
        Task winner = await Task.WhenAny(
            check,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
        );
        bool completed = winner == check;

        releaseDispose.Set();
        await forget.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        bool stillRunning = completed && await check;

        Assert.True(
            completed,
            "IsRunningRemotely was blocked, so _lock was held across RepairContext.Dispose."
        );
        Assert.True(stillRunning);

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_WhenWorkerCancelsRequestMidServe_KeepsServingSubsequentTasks()
    {
        // Regression guard: a request-token cancellation used to break ExecuteAsync's outer loop,
        // silently killing the distributed processor until the application restarted.
        var serviceCts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        var requestCts = new CancellationTokenSource();
        bool firstClaim = true;
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                if (firstClaim)
                {
                    firstClaim = false;
                    // Simulate a worker disconnecting while the claim is in flight: the request
                    // token is cancelled and the in-flight await observes it.
                    await requestCts.CancelAsync();
                    throw new OperationCanceledException((CancellationToken)ci[1]);
                }

                return true;
            });

        var processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );
        await processor.StartAsync(serviceCts.Token);

        PersistedTask? first = await processor.GetTask(requestCts.Token);
        Assert.Null(first);

        // The processor must still be alive to serve a subsequent request.
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(2, 1));
        PersistedTask? second = await processor.GetTask(serviceCts.Token);

        Assert.NotNull(second);
        Assert.Equal(2, ((DetectSplitCandidatesTask)second!.Data).ChapterId);

        await serviceCts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForgetTask_WhileRepairCompletionHoldsLease_DefersContextDispose()
    {
        // Regression guard: cancel/forget used to dispose the RepairContext and delete its work
        // directory while an in-flight completion was still using it.
        var disposeStarted = new ManualResetEventSlim(false);
        var repairContext = new SignalingRepairContext(disposeStarted);
        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            $"repair-context-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(workDirectory);
        repairContext.WorkDirectory = workDirectory;

        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[1] = new PersistedTask { Id = 1, Data = new RepairUpscaleTask() };
        Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState> repairStates =
            GetPrivateField<Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState>>(
                _processor,
                "remoteRepairStates"
            );
        var state = new DistributedUpscaleTaskProcessor.RemoteRepairState
        {
            RepairContext = repairContext,
        };
        repairStates[1] = state;

        // Simulate an in-flight completion that already acquired the state.
        Assert.True(state.TryBeginUse());

        // Act: forget the task while the completion still holds the lease.
        _processor.ForgetTask(1);

        // Assert: the context must not be disposed yet.
        Assert.False(
            disposeStarted.Wait(
                TimeSpan.FromMilliseconds(250),
                TestContext.Current.CancellationToken
            )
        );

        // Releasing the lease performs the deferred cleanup.
        state.EndUse(Substitute.For<ILogger>());

        Assert.True(
            disposeStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
        );

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReapDeadTasks_WhenOneRequeueFails_LeavesItRecoverableAndRequeuesTheRest()
    {
        // Regression guard: the reaper removed every dead task from runningTasks before requeueing,
        // so a single per-task failure dropped the rest and stranded them until restart.
        var failing = new PersistedTask
        {
            Id = 9001,
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = PersistedTaskStatus.Processing,
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-5),
        };
        var healthy = new PersistedTask
        {
            Id = 9002,
            Data = new DetectSplitCandidatesTask(2, 1),
            Status = PersistedTaskStatus.Processing,
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-5),
        };

        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[failing.Id] = failing;
        runningTasks[healthy.Id] = healthy;

        int failingAttempts = 0;
        _mockPersistence
            .RequeueStrandedTaskAsync(failing.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Fail the first tick only; the next tick must retry and succeed.
                if (Interlocked.Increment(ref failingAttempts) == 1)
                {
                    throw new InvalidOperationException("requeue failed");
                }

                return 1;
            });
        _mockPersistence
            .RequeueStrandedTaskAsync(healthy.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        await _processor.ReapDeadTasksAsync(TestContext.Current.CancellationToken);

        // The failed task stays tracked for the next tick and was not requeued.
        Assert.True(runningTasks.ContainsKey(failing.Id));
        Assert.DoesNotContain(_taskQueue.GetUpscaleSnapshot(), t => t.Id == failing.Id);

        // The healthy task was requeued and is no longer tracked.
        Assert.False(runningTasks.ContainsKey(healthy.Id));
        Assert.Contains(_taskQueue.GetUpscaleSnapshot(), t => t.Id == healthy.Id);
        Assert.Equal(PersistedTaskStatus.Pending, healthy.Status);

        // A later reaper tick retries the task that failed and requeues it.
        await _processor.ReapDeadTasksAsync(TestContext.Current.CancellationToken);

        Assert.False(runningTasks.ContainsKey(failing.Id));
        Assert.Contains(_taskQueue.GetUpscaleSnapshot(), t => t.Id == failing.Id);
        Assert.Equal(PersistedTaskStatus.Pending, failing.Status);
        Assert.Equal(2, failingAttempts);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        return (T)
            target
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(target)!;
    }

    private sealed class BlockingRepairContext(
        ManualResetEventSlim started,
        ManualResetEventSlim release
    ) : RepairContext
    {
        protected override void DeleteWorkDirectory()
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class SignalingRepairContext(ManualResetEventSlim disposed) : RepairContext
    {
        protected override void DeleteWorkDirectory() => disposed.Set();
    }
}
