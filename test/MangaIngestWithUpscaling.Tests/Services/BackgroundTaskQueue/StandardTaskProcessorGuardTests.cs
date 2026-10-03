using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
}
