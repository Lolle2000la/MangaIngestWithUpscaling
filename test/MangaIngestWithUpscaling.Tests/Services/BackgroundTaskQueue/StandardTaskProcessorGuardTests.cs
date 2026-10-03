using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
/// The <see cref="ApplySplitsTask"/> guard that keeps it from running concurrently with a
/// same-chapter upscale. The guard queries the JSON <c>Data</c> column with provider-specific SQL, so
/// it is exercised on the relational test database (SQLite and PostgreSQL), not the in-memory
/// provider.
/// </summary>
public class StandardTaskProcessorGuardTests : IAsyncDisposable
{
    private readonly TestDatabase _database;

    public StandardTaskProcessorGuardTests()
    {
        _database = TestDatabaseFactory.Create();
        // Create the schema once up front; later contexts reuse it.
        using (ApplicationDbContext schema = _database.CreateContext()) { }
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasSameChapterUpscaleTaskAsync_BlocksOnlyForASameChapterUpscale()
    {
        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        var upscaleTask = new PersistedTask
        {
            Data = new UpscaleTask { ChapterId = 7, UpscalerProfileId = 1 },
            Status = PersistedTaskStatus.Pending,
            Order = 1,
        };
        context.PersistedTasks.AddRange(
            upscaleTask,
            new PersistedTask
            {
                Data = new ApplySplitsTask(7, 1),
                Status = PersistedTaskStatus.Pending,
                Order = 2,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A pending same-chapter upscale blocks the apply...
        Assert.True(
            await StandardTaskProcessor.HasSameChapterUpscaleTaskAsync(
                context,
                7,
                TestContext.Current.CancellationToken
            )
        );
        // ...but a different chapter is unaffected.
        Assert.False(
            await StandardTaskProcessor.HasSameChapterUpscaleTaskAsync(
                context,
                8,
                TestContext.Current.CancellationToken
            )
        );

        // Once the upscale is terminal, the apply may run.
        upscaleTask.Status = PersistedTaskStatus.Completed;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(
            await StandardTaskProcessor.HasSameChapterUpscaleTaskAsync(
                context,
                7,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasSameChapterApplyTaskAsync_BlocksOnlyForAProcessingSameChapterApply()
    {
        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );

        var pendingApply = new PersistedTask
        {
            Data = new ApplySplitsTask(7, 1),
            Status = PersistedTaskStatus.Pending,
            Order = 1,
        };
        context.PersistedTasks.AddRange(
            pendingApply,
            new PersistedTask
            {
                Data = new ApplySplitsTask(8, 1),
                Status = PersistedTaskStatus.Processing,
                Order = 2,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A Pending apply does not block: the apply side already defers under a same-chapter upscale,
        // so treating a Pending apply as a blocker here would deadlock the two.
        Assert.False(
            await StandardTaskProcessor.HasSameChapterApplyTaskAsync(
                context,
                7,
                TestContext.Current.CancellationToken
            )
        );
        // A Processing same-chapter apply blocks an upscale...
        Assert.True(
            await StandardTaskProcessor.HasSameChapterApplyTaskAsync(
                context,
                8,
                TestContext.Current.CancellationToken
            )
        );
        // ...but a different chapter is unaffected.
        Assert.False(
            await StandardTaskProcessor.HasSameChapterApplyTaskAsync(
                context,
                9,
                TestContext.Current.CancellationToken
            )
        );

        pendingApply.Status = PersistedTaskStatus.Processing;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(
            await StandardTaskProcessor.HasSameChapterApplyTaskAsync(
                context,
                7,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChapterGate_SerializesTheSameChapterAndAllowsDifferentChapters()
    {
        // The gate makes the guard-then-claim atomic across the standard and upscale processors; this
        // pins that the same chapter is mutually exclusive while a different chapter is not.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        using ServiceProvider provider = services.BuildServiceProvider();

        var taskQueue = new TaskQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<TaskQueue>>()
        );

        IDisposable first = await taskQueue.AcquireChapterGateAsync(
            7,
            TestContext.Current.CancellationToken
        );

        Task<IDisposable> blocked = taskQueue.AcquireChapterGateAsync(
            7,
            TestContext.Current.CancellationToken
        );
        // The same chapter cannot proceed while the gate is held (no timing dependency).
        Assert.False(blocked.IsCompleted);

        // A different chapter is not serialized behind it.
        using (
            IDisposable other = await taskQueue.AcquireChapterGateAsync(
                8,
                TestContext.Current.CancellationToken
            )
        )
        {
            Assert.NotNull(other);
        }

        first.Dispose();
        using IDisposable second = await blocked.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpscaleProcessor_DefersAnUpscaleWhileASameChapterApplyIsProcessing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        using ServiceProvider provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var taskQueue = new TaskQueue(
            scopeFactory,
            provider.GetRequiredService<ILogger<TaskQueue>>()
        );
        var processor = new UpscaleTaskProcessor(
            taskQueue,
            scopeFactory,
            Options.Create(new UpscalerConfig { RemoteOnly = false }),
            provider.GetRequiredService<ILogger<UpscaleTaskProcessor>>(),
            new TaskPersistenceService(scopeFactory),
            new PreprocessedInputCache()
        );

        // A same-chapter apply is already in flight (rewriting the CBZ the upscale would stream from).
        await using (
            ApplicationDbContext seed = await _database.CreateContextAsync(
                TestContext.Current.CancellationToken
            )
        )
        {
            seed.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = new ApplySplitsTask(7, 1),
                    Status = PersistedTaskStatus.Processing,
                    Order = 0,
                }
            );
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 7, UpscalerProfileId = 1 });
        int upscaleId = taskQueue.GetUpscaleSnapshot().Single().Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            await using ApplicationDbContext context = await _database.CreateContextAsync(
                TestContext.Current.CancellationToken
            );
            PersistedTask upscale = await context
                .PersistedTasks.AsNoTracking()
                .SingleAsync(t => t.Id == upscaleId, TestContext.Current.CancellationToken);

            // Deferred, not claimed: it stays Pending with no retry consumed.
            Assert.Equal(PersistedTaskStatus.Pending, upscale.Status);
            Assert.Equal(0, upscale.RetryCount);
        }
        finally
        {
            await cts.CancelAsync();
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Processor_DefersApplySplitsWhileASameChapterUpscaleIsPending()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        using ServiceProvider provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var taskQueue = new TaskQueue(
            scopeFactory,
            provider.GetRequiredService<ILogger<TaskQueue>>()
        );
        var processor = new StandardTaskProcessor(
            taskQueue,
            scopeFactory,
            provider.GetRequiredService<ILogger<StandardTaskProcessor>>(),
            new TaskPersistenceService(scopeFactory)
        );

        // The upscale is enqueued first and stays Pending (no worker consumes the upscale queue here),
        // so the standard processor must defer the apply rather than claim it.
        await taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 7, UpscalerProfileId = 1 });
        await taskQueue.EnqueueAsync(new ApplySplitsTask(7, 1));
        int applyId = taskQueue.GetStandardSnapshot().Single().Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            await using ApplicationDbContext context = await _database.CreateContextAsync(
                TestContext.Current.CancellationToken
            );
            PersistedTask apply = await context
                .PersistedTasks.AsNoTracking()
                .SingleAsync(t => t.Id == applyId, TestContext.Current.CancellationToken);

            Assert.Equal(PersistedTaskStatus.Pending, apply.Status);
            Assert.Equal(0, apply.RetryCount);
        }
        finally
        {
            await cts.CancelAsync();
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Processor_RetriesADeferredApplyOnceTheBlockerFinishes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));
        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        using ServiceProvider provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var taskQueue = new TaskQueue(
            scopeFactory,
            provider.GetRequiredService<ILogger<TaskQueue>>()
        );
        var processor = new FastDeferralStandardTaskProcessor(
            taskQueue,
            scopeFactory,
            provider.GetRequiredService<ILogger<StandardTaskProcessor>>(),
            new TaskPersistenceService(scopeFactory)
        );

        await taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 7, UpscalerProfileId = 1 });
        await taskQueue.EnqueueAsync(new ApplySplitsTask(7, 1));
        int applyId = taskQueue.GetStandardSnapshot().Single().Id;

        var claimed = new TaskCompletionSource();
        processor.StatusChanged += task =>
        {
            if (task.Id == applyId && task.Status == PersistedTaskStatus.Processing)
            {
                claimed.TrySetResult();
            }

            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await processor.StartAsync(cts.Token);
        try
        {
            // Deferred while the upscale is pending.
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.False(claimed.Task.IsCompleted);

            // Hold the blocker past the bounded transient-claim retry budget (250ms/1s/3s). Before
            // deferrals had their own re-offer, the apply then sat until the 10-minute periodic replayer.
            await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
            Assert.False(claimed.Task.IsCompleted);

            await using (
                ApplicationDbContext context = await _database.CreateContextAsync(
                    TestContext.Current.CancellationToken
                )
            )
            {
                PersistedTask upscale = await context.PersistedTasks.SingleAsync(
                    t => t.Order == 1,
                    TestContext.Current.CancellationToken
                );
                upscale.Status = PersistedTaskStatus.Completed;
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            // The apply is re-offered promptly instead of waiting for the replayer.
            await claimed.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );
        }
        finally
        {
            await cts.CancelAsync();
            await processor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Processor that re-offers a deferred task promptly, so the test does not wait 10 seconds.</summary>
    private sealed class FastDeferralStandardTaskProcessor(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<StandardTaskProcessor> logger,
        ITaskPersistenceService taskPersistenceService
    ) : StandardTaskProcessor(taskQueue, scopeFactory, logger, taskPersistenceService)
    {
        protected override TimeSpan DeferralRetryInterval => TimeSpan.FromMilliseconds(100);
    }
}
