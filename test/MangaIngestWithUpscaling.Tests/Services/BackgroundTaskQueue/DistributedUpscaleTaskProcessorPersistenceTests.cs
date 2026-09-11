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
}
