using System.Reflection;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
///     Persistence regression tests for the distributed processor. SQLite is used instead of the
///     in-memory provider because the chapter/skip branches include <c>Library</c>, whose complex
///     type the in-memory provider cannot shape.
/// </summary>
public class DistributedUpscaleTaskProcessorPersistenceTests : IDisposable
{
    private readonly string _dbFile;
    private readonly ServiceProvider _provider;
    private readonly TaskQueue _taskQueue;
    private readonly DistributedUpscaleTaskProcessor _processor;

    public DistributedUpscaleTaskProcessorPersistenceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _dbFile = Path.Combine(
            Path.GetTempPath(),
            $"distributed-persistence-{Guid.NewGuid():N}.db"
        );
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={_dbFile}")
        );

        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = true })
        );
        // HandleRepairTaskCompletion resolves these before it can fail on a missing chapter.
        services.AddScoped(_ => Substitute.For<IRepairService>());
        services.AddScoped(_ => Substitute.For<IMetadataHandlingService>());

        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope
                .ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.EnsureCreated();
        }

        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        var persistence = new TaskPersistenceService(scopeFactory);
        _taskQueue = new TaskQueue(
            scopeFactory,
            _provider.GetRequiredService<ILogger<TaskQueue>>()
        );
        _processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            scopeFactory,
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbFile);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp database.
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_ApplySplitsTaskForMissingChapter_PersistsFailedStatus()
    {
        // Regression guard: the skip branch used to only set the in-memory status, leaving the
        // database row stuck in Processing (and replaying on restart).
        var cts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(new ApplySplitsTask(999_999, 1));
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        int failingTaskId = _taskQueue
            .GetUpscaleSnapshot()
            .Single(t => t.Data is ApplySplitsTask)
            .Id;

        Task runTask = _processor.StartAsync(cts.Token);
        PersistedTask? handedToRemote = await _processor.GetTask(cts.Token);

        Assert.NotNull(handedToRemote);
        Assert.IsType<DetectSplitCandidatesTask>(handedToRemote!.Data);
        Assert.Equal(PersistedTaskStatus.Failed, await GetStatusAsync(failingTaskId));

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_RepairTaskForMissingChapter_PersistsFailedStatus()
    {
        var cts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(
            new RepairUpscaleTask { ChapterId = 999_999, UpscalerProfileId = 999_999 }
        );
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        int repairTaskId = _taskQueue
            .GetUpscaleSnapshot()
            .Single(t => t.Data is RepairUpscaleTask)
            .Id;

        Task runTask = _processor.StartAsync(cts.Token);
        PersistedTask? handedToRemote = await _processor.GetTask(cts.Token);

        Assert.NotNull(handedToRemote);
        Assert.IsType<DetectSplitCandidatesTask>(handedToRemote!.Data);
        Assert.Equal(PersistedTaskStatus.Failed, await GetStatusAsync(repairTaskId));

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskCompleted_WhenRepairCompletionFails_PersistsTerminalFailedStatus()
    {
        // Regression guard: a failed repair completion only left the in-memory status alone and
        // the row stayed Processing until the next startup reset.
        int taskId;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = new PersistedTask
            {
                Data = new RepairUpscaleTask { ChapterId = 999_999, UpscalerProfileId = 999_999 },
                Status = PersistedTaskStatus.Processing,
                Order = 1,
            };
            db.PersistedTasks.Add(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        await _processor.TaskCompleted(taskId);

        Assert.Equal(PersistedTaskStatus.Failed, await GetStatusAsync(taskId));
        Assert.False(_processor.IsRunningRemotely(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ClaimTaskAsync_SecondClaimOfPendingRow_ReturnsFalseAndLeavesProcessing()
    {
        // Regression guard: the claim was a non-atomic read-check-write. A second claim of an
        // already-claimed row must fail and must not change the row.
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Pending);
        var persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );

        bool first = await persistence.ClaimTaskAsync(
            taskId,
            TestContext.Current.CancellationToken
        );
        bool second = await persistence.ClaimTaskAsync(
            taskId,
            TestContext.Current.CancellationToken
        );

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(PersistedTaskStatus.Processing, await GetStatusAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ClaimTaskAsync_ConcurrentClaimsOfSamePendingRow_OnlyOneSucceeds()
    {
        // The claim is a single guarded UPDATE, so two claimers racing on the same Pending row must
        // not both succeed and run the task twice.
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Pending);
        var persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );

        using var barrier = new Barrier(2);
        Task<bool> first = Task.Run(
            async () =>
            {
                barrier.SignalAndWait();
                return await persistence.ClaimTaskAsync(
                    taskId,
                    TestContext.Current.CancellationToken
                );
            },
            TestContext.Current.CancellationToken
        );
        Task<bool> second = Task.Run(
            async () =>
            {
                barrier.SignalAndWait();
                return await persistence.ClaimTaskAsync(
                    taskId,
                    TestContext.Current.CancellationToken
                );
            },
            TestContext.Current.CancellationToken
        );

        bool[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(PersistedTaskStatus.Processing, await GetStatusAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReapDeadTasks_WhenRowBecameTerminal_DoesNotResurrectIt()
    {
        // Regression guard: the reaper used the unguarded RetryAsync, so a row that completed or was
        // canceled while the task was dead could be flipped back to Pending and re-run.
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Completed);
        var deadTask = new PersistedTask
        {
            Id = taskId,
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = PersistedTaskStatus.Processing,
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-5),
        };
        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[taskId] = deadTask;

        await _processor.ReapDeadTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistedTaskStatus.Completed, await GetStatusAsync(taskId));
        Assert.DoesNotContain(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
        Assert.False(runningTasks.ContainsKey(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelCurrent_WhenRowAlreadyTerminal_DoesNotEmitContradictoryCanceledStatus()
    {
        // Regression guard: CancelCurrent marked the in-memory task Canceled and raised
        // StatusChanged even when the guarded cancel affected no row because the row was already
        // terminal, telling the UI a status the database never had.
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Completed);
        var workTask = new PersistedTask
        {
            Id = taskId,
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = PersistedTaskStatus.Processing,
        };
        Dictionary<int, PersistedTask> runningTasks = GetPrivateField<
            Dictionary<int, PersistedTask>
        >(_processor, "runningTasks");
        runningTasks[taskId] = workTask;

        var emitted = new List<PersistedTaskStatus>();
        _processor.StatusChanged += task =>
        {
            lock (emitted)
            {
                emitted.Add(task.Status);
            }

            return Task.CompletedTask;
        };

        await _processor.CancelCurrent(workTask);

        Assert.Equal(PersistedTaskStatus.Processing, workTask.Status);
        lock (emitted)
        {
            Assert.DoesNotContain(PersistedTaskStatus.Canceled, emitted);
        }
        Assert.Equal(PersistedTaskStatus.Completed, await GetStatusAsync(taskId));
        Assert.False(_processor.IsRunningRemotely(taskId));
    }

    private async Task<PersistedTaskStatus?> GetStatusAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? task = await db
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, TestContext.Current.CancellationToken);
        return task?.Status;
    }

    private async Task<int> GetRetryCountAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? task = await db
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, TestContext.Current.CancellationToken);
        return task?.RetryCount ?? -1;
    }

    private async Task<PersistedTaskStatus?> WaitForStatusAsync(
        int id,
        PersistedTaskStatus expected
    )
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        PersistedTaskStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await GetStatusAsync(id);
            if (status == expected)
            {
                return status;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return status;
    }

    private async Task<int> SeedTaskAsync(PersistedTaskStatus status, int retryCount = 0)
    {
        using IServiceScope scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var task = new PersistedTask
        {
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = status,
            RetryCount = retryCount,
            Order = 1,
        };
        db.PersistedTasks.Add(task);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return task.Id;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareRepairTaskForRemote_WhenWorkerCancelsPreparation_RequeuesWithoutIncrementingRetry()
    {
        // A worker disconnect is an infrastructure event, not a task failure: the task must be
        // requeued promptly without consuming its RetryFor budget.
        int taskId = await SeedRepairTaskAsync(retryFor: 3, retryCount: 0);

        bool prepared = await InvokePrepareRepairTaskForRemoteAsync(taskId);

        Assert.False(prepared);
        Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
        Assert.Contains(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareRepairTaskForRemote_WhenWorkerCancelsRepeatedly_NeverFails()
    {
        // Repeated worker disconnects must never terminal-fail the task nor consume its retry budget.
        int taskId = await SeedRepairTaskAsync(retryCount: 0);

        for (int i = 0; i < 5; i++)
        {
            Assert.False(await InvokePrepareRepairTaskForRemoteAsync(taskId));
            Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
            Assert.Equal(0, await GetRetryCountAsync(taskId));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareRepairTaskForRemote_WhenWorkerCancelsWithNoRetryBudget_StaysPending()
    {
        // A RetryFor of 1 must not turn an infrastructure event into a terminal failure.
        int taskId = await SeedRepairTaskAsync(retryFor: 1, retryCount: 0);

        bool prepared = await InvokePrepareRepairTaskForRemoteAsync(taskId);

        Assert.False(prepared);
        Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
        Assert.Contains(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequeueClaimedTask_WhenServiceIsStopping_DoesNotConsumeRetryBudgetOrFail()
    {
        // Service shutdown is not a task failure: it must not consume the retry budget and must not
        // turn the task into a terminal failure.
        int taskId = await SeedRepairTaskAsync(retryFor: 1, retryCount: 0);
        var stoppingToken = new CancellationToken(canceled: true);
        typeof(DistributedUpscaleTaskProcessor)
            .GetField("serviceStoppingToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_processor, stoppingToken);

        PersistedTask persistedTask;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            persistedTask = await db
                .PersistedTasks.AsNoTracking()
                .FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
        }

        MethodInfo method = typeof(DistributedUpscaleTaskProcessor).GetMethod(
            "RequeueClaimedTaskAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        await (Task)method.Invoke(_processor, new object[] { persistedTask, stoppingToken })!;

        Assert.NotEqual(PersistedTaskStatus.Failed, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequeueClaimedTask_WhenRowIsTerminal_ReturnsSuccessWithoutResurrectingIt()
    {
        // A row that reached a terminal state while its claim was being handed off must be dropped,
        // not flipped back to Pending: the recovery reports success because there is nothing left
        // to recover.
        int taskId = await SeedRepairTaskAsync(retryFor: 1, retryCount: 0);
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            PersistedTask row = await db.PersistedTasks.FirstAsync(
                t => t.Id == taskId,
                TestContext.Current.CancellationToken
            );
            row.Status = PersistedTaskStatus.Canceled;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        PersistedTask persistedTask = await GetDetachedTaskAsync(taskId);

        bool recovered = await InvokeRequeueClaimedTaskAsync(persistedTask);

        Assert.True(recovered);
        Assert.Equal(PersistedTaskStatus.Canceled, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
        Assert.DoesNotContain(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequeueClaimedTask_WhenRowWasRemoved_ReturnsSuccessWithoutResurrectingIt()
    {
        // A concurrently removed row must be dropped, not recreated by the requeue path.
        int taskId = await SeedRepairTaskAsync(retryFor: 1, retryCount: 0);
        PersistedTask persistedTask = await GetDetachedTaskAsync(taskId);
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            PersistedTask row = await db.PersistedTasks.FirstAsync(
                t => t.Id == taskId,
                TestContext.Current.CancellationToken
            );
            db.PersistedTasks.Remove(row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool recovered = await InvokeRequeueClaimedTaskAsync(persistedTask);

        Assert.True(recovered);
        Assert.Null(await GetStatusAsync(taskId));
        Assert.Equal(-1, await GetRetryCountAsync(taskId));
        Assert.DoesNotContain(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequeueStrandedTaskAsync_OnTerminalRow_ReturnsZeroAndLeavesRetryCountUntouched()
    {
        // Terminal rows are the source of truth: the recovery must refuse them and must never touch
        // the RetryFor budget.
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Canceled, retryCount: 2);
        var persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );

        int affected = await persistence.RequeueStrandedTaskAsync(
            taskId,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, affected);
        Assert.Equal(PersistedTaskStatus.Canceled, await GetStatusAsync(taskId));
        Assert.Equal(2, await GetRetryCountAsync(taskId));
    }

    private async Task<bool> InvokeRequeueClaimedTaskAsync(PersistedTask persistedTask)
    {
        MethodInfo method = typeof(DistributedUpscaleTaskProcessor).GetMethod(
            "RequeueClaimedTaskAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var invocation = method.Invoke(
            _processor,
            new object[] { persistedTask, TestContext.Current.CancellationToken }
        )!;
        return await (Task<bool>)invocation;
    }

    private async Task<PersistedTask> GetDetachedTaskAsync(int taskId)
    {
        using IServiceScope scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db
            .PersistedTasks.AsNoTracking()
            .FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
    }

    private async Task<int> SeedRepairTaskAsync(int? retryFor = null, int retryCount = 0)
    {
        using IServiceScope scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var data = new RepairUpscaleTask { ChapterId = 999_999, UpscalerProfileId = 999_999 };
        if (retryFor.HasValue)
        {
            data.RetryFor = retryFor.Value;
        }

        var task = new PersistedTask
        {
            Data = data,
            Status = PersistedTaskStatus.Processing,
            RetryCount = retryCount,
            Order = 1,
        };
        db.PersistedTasks.Add(task);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return task.Id;
    }

    private async Task<bool> InvokePrepareRepairTaskForRemoteAsync(int taskId)
    {
        PersistedTask persistedTask;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            persistedTask = await db
                .PersistedTasks.AsNoTracking()
                .FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using IServiceScope methodScope = _provider.CreateScope();
        MethodInfo method = typeof(DistributedUpscaleTaskProcessor).GetMethod(
            "PrepareRepairTaskForRemote",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var invocation = method.Invoke(
            _processor,
            new object[]
            {
                (RepairUpscaleTask)persistedTask.Data,
                persistedTask,
                methodScope.ServiceProvider,
                cancelled.Token,
            }
        );

        return await (Task<bool>)invocation!;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailTaskAsync_AfterCancel_DoesNotOverwriteTerminalStateOrInflateRetries()
    {
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Canceled);
        var persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );

        await persistence.FailTaskAsync(taskId, TestContext.Current.CancellationToken);

        Assert.Equal(PersistedTaskStatus.Canceled, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailTaskAsync_CalledTwice_IncrementsRetryCountOnce()
    {
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Processing);
        var persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );

        await persistence.FailTaskAsync(taskId, TestContext.Current.CancellationToken);
        await persistence.FailTaskAsync(taskId, TestContext.Current.CancellationToken);

        Assert.Equal(PersistedTaskStatus.Failed, await GetStatusAsync(taskId));
        Assert.Equal(1, await GetRetryCountAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteTaskAsync_AfterFailure_DoesNotOverwriteTerminalState()
    {
        int taskId = await SeedTaskAsync(PersistedTaskStatus.Failed, retryCount: 1);
        var persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );

        await persistence.CompleteTaskAsync(taskId, TestContext.Current.CancellationToken);

        Assert.Equal(PersistedTaskStatus.Failed, await GetStatusAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_WhenWorkerCancelsAfterClaim_RequeuesTaskInsteadOfStrandingIt()
    {
        // Regression guard: a task claimed by the distributed processor was removed from the
        // in-memory set and never added to runningTasks, so a worker disconnect before handoff left
        // its row stranded in Processing (the reaper only scans runningTasks and the replay ignores
        // Processing).
        var serviceCts = new CancellationTokenSource();
        using var requestCts = new CancellationTokenSource();

        var realPersistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );
        var persistence = Substitute.For<ITaskPersistenceService>();
        bool cancelOnFirstClaim = true;
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                bool claimed = await realPersistence.ClaimTaskAsync(
                    (int)ci[0],
                    (CancellationToken)ci[1]
                );
                if (cancelOnFirstClaim)
                {
                    cancelOnFirstClaim = false;
                    // Simulate the worker disconnecting after the claim succeeded but before the
                    // task could be handed off.
                    await requestCts.CancelAsync();
                }

                return claimed;
            });
        persistence
            .CompleteTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => realPersistence.CompleteTaskAsync((int)ci[0], (CancellationToken)ci[1]));
        persistence
            .FailTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => realPersistence.FailTaskAsync((int)ci[0], (CancellationToken)ci[1]));
        persistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                realPersistence.CancelTaskAsync((int)ci[0], (bool)ci[1], (CancellationToken)ci[2])
            );
        persistence
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                realPersistence.RequeueStrandedTaskAsync((int)ci[0], (CancellationToken)ci[1])
            );

        var processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );

        // ApplySplits performs a chapter query after the claim, which observes the cancelled token.
        // A disconnect is an infrastructure event, so it is requeued regardless of RetryFor.
        await _taskQueue.EnqueueAsync(new ApplySplitsTask(999_999, 1) { RetryFor = 1 });
        int taskId = _taskQueue.GetUpscaleSnapshot().Single().Id;

        await processor.StartAsync(serviceCts.Token);

        PersistedTask? first = await processor.GetTask(requestCts.Token);
        Assert.Null(first);

        // GetTask returns as soon as the request token is cancelled, which can happen while the
        // claim is still completing; wait for the processor's recovery to settle.
        PersistedTaskStatus? status = await WaitForStatusAsync(taskId, PersistedTaskStatus.Pending);

        // The claim was persisted as Processing, then the cancellation must put it back to Pending
        // so it is recoverable rather than stranded, without consuming the retry budget.
        Assert.Equal(PersistedTaskStatus.Pending, status);
        Assert.Equal(0, await GetRetryCountAsync(taskId));
        // The missing-chapter skip branch would have failed it; the requeue path must not have run.
        await persistence
            .DidNotReceive()
            .FailTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        // The processor must keep serving later requests.
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));
        PersistedTask? second = await processor.GetTask(serviceCts.Token);
        Assert.NotNull(second);
        Assert.IsType<DetectSplitCandidatesTask>(second!.Data);

        await serviceCts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_WhenHandoffFails_RequeuesWithoutIncrementingRetry()
    {
        // A requester that cancels while the processor hands off the task is an infrastructure event:
        // it must be requeued promptly without consuming the retry budget.
        int taskId = await RunHandoffFailureAsync(retryFor: 3);

        Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
        Assert.Contains(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_WhenHandoffFailsWithLowRetryBudget_DoesNotFail()
    {
        // A low RetryFor must not turn a failed handoff into a terminal failure; the infrastructure
        // recovery keeps returning the task to Pending.
        int taskId = await RunHandoffFailureAsync(retryFor: 1);

        Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
        Assert.Contains(_taskQueue.GetUpscaleSnapshot(), t => t.Id == taskId);
    }

    /// <summary>
    ///     Drives the distributed processor through the handoff-failure branch: the requester's
    ///     token is cancelled after the claim succeeds but before it can accept the task, so
    ///     <c>tcs.TrySetResult</c> fails. Returns the task id.
    /// </summary>
    private async Task<int> RunHandoffFailureAsync(int retryFor)
    {
        var serviceCts = new CancellationTokenSource();
        using var requestCts = new CancellationTokenSource();

        var realPersistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                bool claimed = await realPersistence.ClaimTaskAsync(
                    (int)ci[0],
                    (CancellationToken)ci[1]
                );
                // The requester disconnects after the claim succeeded but before it can accept
                // the task at handoff.
                await requestCts.CancelAsync();
                return claimed;
            });
        persistence
            .CompleteTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => realPersistence.CompleteTaskAsync((int)ci[0], (CancellationToken)ci[1]));
        persistence
            .FailTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => realPersistence.FailTaskAsync((int)ci[0], (CancellationToken)ci[1]));
        persistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                realPersistence.CancelTaskAsync((int)ci[0], (bool)ci[1], (CancellationToken)ci[2])
            );
        persistence
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                realPersistence.RequeueStrandedTaskAsync((int)ci[0], (CancellationToken)ci[1])
            );

        var processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );

        // DetectSplitCandidatesTask has no post-claim database query, so the cancelled request
        // reaches the handoff branch instead of throwing earlier.
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1) { RetryFor = retryFor });
        int taskId = _taskQueue.GetUpscaleSnapshot().Single().Id;

        await processor.StartAsync(serviceCts.Token);

        PersistedTask? first = await processor.GetTask(requestCts.Token);
        Assert.Null(first);

        // Wait for the recovery write to settle. The failed handoff must always return the task to
        // Pending, regardless of its RetryFor budget.
        await WaitForStatusAsync(taskId, PersistedTaskStatus.Pending);

        await serviceCts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);

        return taskId;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_WhenClaimThrowsAfterCommit_RestoresRowAndKeepsServing()
    {
        // Regression guard: ClaimTaskAsync can throw after the row was already committed to
        // Processing. claimedTask used to be assigned only after a successful claim, so that error
        // was recovered by nothing: the task was no longer in the in-memory set, never entered
        // runningTasks, and its row stayed Processing until restart.
        var serviceCts = new CancellationTokenSource();
        var realPersistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );
        var persistence = Substitute.For<ITaskPersistenceService>();
        bool firstClaim = true;
        persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                if (firstClaim)
                {
                    firstClaim = false;
                    // Commit the claim, then fail before the processor can track it.
                    await realPersistence.ClaimTaskAsync((int)ci[0], (CancellationToken)ci[1]);
                    throw new InvalidOperationException("claim failed after commit");
                }

                return await realPersistence.ClaimTaskAsync((int)ci[0], (CancellationToken)ci[1]);
            });
        persistence
            .CompleteTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => realPersistence.CompleteTaskAsync((int)ci[0], (CancellationToken)ci[1]));
        persistence
            .FailTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => realPersistence.FailTaskAsync((int)ci[0], (CancellationToken)ci[1]));
        persistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                realPersistence.CancelTaskAsync((int)ci[0], (bool)ci[1], (CancellationToken)ci[2])
            );
        persistence
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                realPersistence.RequeueStrandedTaskAsync((int)ci[0], (CancellationToken)ci[1])
            );

        var processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );

        // ApplySplits performs a chapter query after the claim; when the recovered task is
        // re-claimed it fails on its missing chapter before the next task is handed to the worker.
        // A post-commit claim failure is an infrastructure event and is requeued regardless of the
        // task's RetryFor budget.
        await _taskQueue.EnqueueAsync(new ApplySplitsTask(999_999, 1) { RetryFor = 1 });
        int taskId = _taskQueue.GetUpscaleSnapshot().Single().Id;

        await processor.StartAsync(serviceCts.Token);

        // The claim exception is surfaced to the requesting worker.
        await Assert.ThrowsAnyAsync<Exception>(() => processor.GetTask(serviceCts.Token));

        // The row must be recoverable (Pending), never stranded Processing, and without consuming
        // the retry budget.
        Assert.Equal(
            PersistedTaskStatus.Pending,
            await WaitForStatusAsync(taskId, PersistedTaskStatus.Pending)
        );
        Assert.Equal(0, await GetRetryCountAsync(taskId));

        // The processor keeps serving later requests.
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(2, 1));
        PersistedTask? second = await processor.GetTask(serviceCts.Token);
        Assert.NotNull(second);
        Assert.IsType<DetectSplitCandidatesTask>(second!.Data);

        await serviceCts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskCompleted_WhenRowAlreadyCanceled_DoesNotEmitContradictoryTerminalStatus()
    {
        // Regression guard: TaskCompleted mutated the in-memory task and raised a terminal
        // StatusChanged even when the guarded CompleteTaskAsync changed no row.
        int taskId;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = new PersistedTask
            {
                Data = new DetectSplitCandidatesTask(1, 1),
                Status = PersistedTaskStatus.Canceled,
                Order = 1,
            };
            db.PersistedTasks.Add(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        var emitted = new List<PersistedTaskStatus>();
        _processor.StatusChanged += task =>
        {
            lock (emitted)
            {
                emitted.Add(task.Status);
            }

            return Task.CompletedTask;
        };

        await _processor.TaskCompleted(taskId);

        lock (emitted)
        {
            Assert.DoesNotContain(
                emitted,
                status =>
                    status == PersistedTaskStatus.Completed || status == PersistedTaskStatus.Failed
            );
        }

        Assert.Equal(PersistedTaskStatus.Canceled, await GetStatusAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TaskCompleted_WhenRepairCompletionFailsButRowAlreadyCanceled_DoesNotEmitContradictoryStatus()
    {
        // Covers the PersistFailedAsync guard: a failed repair completion on an already-terminal
        // row must not overwrite the in-memory/UI state.
        int taskId;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = new PersistedTask
            {
                Data = new RepairUpscaleTask { ChapterId = 999_999, UpscalerProfileId = 999_999 },
                Status = PersistedTaskStatus.Canceled,
                Order = 1,
            };
            db.PersistedTasks.Add(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        var emitted = new List<PersistedTaskStatus>();
        _processor.StatusChanged += task =>
        {
            lock (emitted)
            {
                emitted.Add(task.Status);
            }

            return Task.CompletedTask;
        };

        await _processor.TaskCompleted(taskId);

        lock (emitted)
        {
            Assert.DoesNotContain(
                emitted,
                status =>
                    status == PersistedTaskStatus.Completed || status == PersistedTaskStatus.Failed
            );
        }

        Assert.Equal(PersistedTaskStatus.Canceled, await GetStatusAsync(taskId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareRepairTaskForRemote_WithStaleState_DisposesItBeforePreparing()
    {
        // Regression guard: a requeued dead repair task could still hold a RemoteRepairState from
        // its previous attempt, and preparation overwrote it without disposing the old context.
        int taskId;
        using (IServiceScope seedScope = _provider.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = new PersistedTask
            {
                Data = new RepairUpscaleTask { ChapterId = 999_999, UpscalerProfileId = 999_999 },
                Status = PersistedTaskStatus.Processing,
                Order = 1,
            };
            db.PersistedTasks.Add(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        string workDirectory = Path.Combine(Path.GetTempPath(), $"repair-f4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var disposed = new ManualResetEventSlim(false);
        var staleContext = new SignalingRepairContext(disposed) { WorkDirectory = workDirectory };

        Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState> states = GetPrivateField<
            Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState>
        >(_processor, "remoteRepairStates");
        states[taskId] = new DistributedUpscaleTaskProcessor.RemoteRepairState
        {
            RepairContext = staleContext,
        };

        PersistedTask persistedTask;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            persistedTask = await db
                .PersistedTasks.AsNoTracking()
                .FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
        }

        using IServiceScope methodScope = _provider.CreateScope();
        MethodInfo method = typeof(DistributedUpscaleTaskProcessor).GetMethod(
            "PrepareRepairTaskForRemote",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var invocation = method.Invoke(
            _processor,
            new object[]
            {
                (RepairUpscaleTask)persistedTask.Data,
                persistedTask,
                methodScope.ServiceProvider,
                TestContext.Current.CancellationToken,
            }
        );
        bool prepared = await (Task<bool>)invocation!;

        Assert.False(prepared);
        Assert.True(
            disposed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "The stale repair context should have been disposed."
        );
        Assert.False(states.ContainsKey(taskId));

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequeueClaimedTask_WhenRepairWasPrepared_CleansUpStateAndTempFiles()
    {
        // Change 2 regression guard: a prepared repair task whose handoff fails and is requeued must
        // not leave remoteRepairStates populated or its temp CBZs behind for the next preparation.
        int taskId;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = new PersistedTask
            {
                Data = new RepairUpscaleTask { ChapterId = 999_999, UpscalerProfileId = 999_999 },
                Status = PersistedTaskStatus.Processing,
                Order = 1,
            };
            db.PersistedTasks.Add(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        string preparedCbz = Path.Combine(
            Path.GetTempPath(),
            $"repair-prepared-{Guid.NewGuid():N}.cbz"
        );
        string upscaledCbz = Path.Combine(
            Path.GetTempPath(),
            $"repair-upscaled-{Guid.NewGuid():N}.cbz"
        );
        await File.WriteAllTextAsync(
            preparedCbz,
            "prepared",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            upscaledCbz,
            "upscaled",
            TestContext.Current.CancellationToken
        );

        Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState> states = GetPrivateField<
            Dictionary<int, DistributedUpscaleTaskProcessor.RemoteRepairState>
        >(_processor, "remoteRepairStates");
        states[taskId] = new DistributedUpscaleTaskProcessor.RemoteRepairState
        {
            PreparedMissingPagesCbzPath = preparedCbz,
            UpscaledMissingPagesCbzPath = upscaledCbz,
        };

        PersistedTask persistedTask;
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            persistedTask = await db
                .PersistedTasks.AsNoTracking()
                .FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
        }

        MethodInfo method = typeof(DistributedUpscaleTaskProcessor).GetMethod(
            "RequeueClaimedTaskAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        await (Task)
            method.Invoke(
                _processor,
                new object[] { persistedTask, TestContext.Current.CancellationToken }
            )!;

        Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
        Assert.False(states.ContainsKey(taskId));
        Assert.False(File.Exists(preparedCbz));
        Assert.False(File.Exists(upscaledCbz));
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        return (T)
            target
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(target)!;
    }

    private sealed class SignalingRepairContext(ManualResetEventSlim disposed) : RepairContext
    {
        protected override void DeleteWorkDirectory() => disposed.Set();
    }
}
