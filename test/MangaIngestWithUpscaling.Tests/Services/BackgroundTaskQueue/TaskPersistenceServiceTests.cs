using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
/// Exercises the guarded bulk updates in <see cref="TaskPersistenceService"/> on both providers.
/// These compare-and-set statements are what stop two workers from claiming the same task or a late
/// callback from overwriting a terminal state, and their affected-row semantics are provider
/// specific, so they must run against PostgreSQL too.
/// </summary>
public class TaskPersistenceServiceTests : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly ServiceProvider _provider;
    private readonly TaskPersistenceService _persistence;

    public TaskPersistenceServiceTests()
    {
        _database = TestDatabaseFactory.Create();
        using (ApplicationDbContext schema = _database.CreateContext()) { }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        _provider = services.BuildServiceProvider();

        _persistence = new TaskPersistenceService(
            _provider.GetRequiredService<IServiceScopeFactory>()
        );
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task ClaimTaskAsync_IsAtomicUnderConcurrency()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // SQLite serialises writers and can surface 'database is locked' here; the SQLite-side race
        // is covered by TaskQueueRemovalConcurrencyTests. This validates the PostgreSQL semantics.
        Assert.SkipWhen(
            TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres,
            "Set TEST_DB_PROVIDER=postgres to exercise cross-connection claim atomicity."
        );

        int taskId = await AddTaskAsync(PersistedTaskStatus.Pending, ct);

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _persistence.ClaimTaskAsync(taskId, ct))
        );

        Assert.Equal(1, results.Count(claimed => claimed));
        Assert.Equal(PersistedTaskStatus.Processing, await GetStatusAsync(taskId, ct));
    }

    [Fact]
    public async Task CompleteTaskAsync_SecondCall_IsANoOp()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int taskId = await AddTaskAsync(PersistedTaskStatus.Processing, ct);

        Assert.Equal(1, await _persistence.CompleteTaskAsync(taskId, ct));
        PersistedTask completed = await GetTaskAsync(taskId, ct);
        Assert.Equal(PersistedTaskStatus.Completed, completed.Status);
        Assert.NotNull(completed.ProcessedAt);

        // A duplicate/late completion must not touch the terminal row.
        Assert.Equal(0, await _persistence.CompleteTaskAsync(taskId, ct));
    }

    [Fact]
    public async Task CancelTaskAsync_DoesNotOverwriteATerminalState()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int taskId = await AddTaskAsync(PersistedTaskStatus.Processing, ct);
        Assert.Equal(1, await _persistence.CompleteTaskAsync(taskId, ct));

        Assert.Equal(0, await _persistence.CancelTaskAsync(taskId, cancellationToken: ct));
        Assert.Equal(PersistedTaskStatus.Completed, await GetStatusAsync(taskId, ct));
    }

    [Fact]
    public async Task FailTaskAsync_IncrementsRetryCountOnceAndBlocksLaterTransitions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int taskId = await AddTaskAsync(PersistedTaskStatus.Processing, ct);

        Assert.Equal(1, await _persistence.FailTaskAsync(taskId, ct));
        PersistedTask failed = await GetTaskAsync(taskId, ct);
        Assert.Equal(PersistedTaskStatus.Failed, failed.Status);
        Assert.Equal(1, failed.RetryCount);

        // A second failure would inflate the retry budget; it must be a no-op.
        Assert.Equal(0, await _persistence.FailTaskAsync(taskId, ct));
        Assert.Equal(1, (await GetTaskAsync(taskId, ct)).RetryCount);
    }

    [Fact]
    public async Task RequeueStrandedTaskAsync_DoesNotResurrectATerminalTask()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int taskId = await AddTaskAsync(PersistedTaskStatus.Processing, ct);
        Assert.Equal(1, await _persistence.CompleteTaskAsync(taskId, ct));

        Assert.Equal(0, await _persistence.RequeueStrandedTaskAsync(taskId, ct));
        Assert.Equal(PersistedTaskStatus.Completed, await GetStatusAsync(taskId, ct));
    }

    [Fact]
    public async Task ClaimTaskAsync_OnlyClaimsPendingRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int processing = await AddTaskAsync(PersistedTaskStatus.Processing, ct);
        int completed = await AddTaskAsync(PersistedTaskStatus.Completed, ct);

        Assert.False(await _persistence.ClaimTaskAsync(processing, ct));
        Assert.False(await _persistence.ClaimTaskAsync(completed, ct));
        Assert.False(await _persistence.ClaimTaskAsync(999_999, ct));
    }

    private async Task<int> AddTaskAsync(PersistedTaskStatus status, CancellationToken ct)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var task = new PersistedTask
        {
            Data = new DetectSplitCandidatesTask(chapterId: 1, detectorVersion: 1),
            Status = status,
            Order = 1,
        };
        context.PersistedTasks.Add(task);
        await context.SaveChangesAsync(ct);
        return task.Id;
    }

    private async Task<PersistedTask> GetTaskAsync(int taskId, CancellationToken ct)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await context.PersistedTasks.SingleAsync(t => t.Id == taskId, ct);
    }

    private async Task<PersistedTaskStatus> GetStatusAsync(int taskId, CancellationToken ct) =>
        (await GetTaskAsync(taskId, ct)).Status;
}
