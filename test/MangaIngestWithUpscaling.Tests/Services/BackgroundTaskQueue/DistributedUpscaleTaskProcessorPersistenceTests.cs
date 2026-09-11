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
    public async Task PrepareRepairTaskForRemote_WhenWorkerCancelsPreparation_RequeuesInsteadOfFailing()
    {
        // Regression guard: a worker disconnect during repair preparation was persisted as a
        // permanent Failure (retry count 1 == RetryFor), so the repair was never replayed.
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

        bool prepared = await (Task<bool>)invocation!;

        // Assert: left replayable, not permanently failed.
        Assert.False(prepared);
        Assert.Equal(PersistedTaskStatus.Pending, await GetStatusAsync(taskId));
        Assert.Equal(0, await GetRetryCountAsync(taskId));
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
        await _taskQueue.EnqueueAsync(new ApplySplitsTask(999_999, 1));
        int taskId = _taskQueue.GetUpscaleSnapshot().Single().Id;

        await processor.StartAsync(serviceCts.Token);

        PersistedTask? first = await processor.GetTask(requestCts.Token);
        Assert.Null(first);

        // GetTask returns as soon as the request token is cancelled, which can happen while the
        // claim is still completing; wait for the processor's recovery to settle.
        PersistedTaskStatus? status = await WaitForStatusAsync(taskId, PersistedTaskStatus.Pending);

        // The claim was persisted as Processing, then the cancellation must put it back to Pending
        // so it is recoverable rather than stranded.
        Assert.Equal(PersistedTaskStatus.Pending, status);
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
        await _taskQueue.EnqueueAsync(new ApplySplitsTask(999_999, 1));
        int taskId = _taskQueue.GetUpscaleSnapshot().Single().Id;

        await processor.StartAsync(serviceCts.Token);

        // The claim exception is surfaced to the requesting worker.
        await Assert.ThrowsAnyAsync<Exception>(() => processor.GetTask(serviceCts.Token));

        // The row must be recoverable (Pending), never stranded Processing.
        Assert.Equal(
            PersistedTaskStatus.Pending,
            await WaitForStatusAsync(taskId, PersistedTaskStatus.Pending)
        );

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
