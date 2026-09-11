using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class UpscaleTaskProcessorRerouteGuardTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_ReroutedTaskWhoseRowWasDeleted_IsSkipped()
    {
        // Regression guard: removal does not reach the local reroute channel, so a rerouted task
        // whose row was deleted must not run (which would recreate side effects for a removed task).
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TaskGuard_{Guid.NewGuid()}")
        );
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = false })
        );
        services.AddSingleton(Substitute.For<ITaskPersistenceService>());

        using ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var processor = new ExposedUpscaleTaskProcessor(
            new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>()),
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            provider.GetRequiredService<ITaskPersistenceService>(),
            new PreprocessedInputCache()
        );

        var recording = new RecordingTask();
        var task = new PersistedTask
        {
            Id = 987654,
            Data = recording,
            Status = PersistedTaskStatus.Processing,
        };

        // Act: the row does not exist in the database.
        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(recording.Processed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_WhenReroutedExistenceCheckThrows_ReturnsTaskToPendingForReplay()
    {
        // Regression guard: an exception from the rerouted-task existence check used to be logged
        // and swallowed, dropping the task from every queue while its row stayed Processing until
        // restart. It must be reconciled to a recoverable state instead.
        var services = new ServiceCollection();
        // A query against an unreachable database makes the existence check throw.
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite("Data Source=/nonexistent-directory/reroute-guard.db")
        );
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = false })
        );
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence
            .RequeueStrandedTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));
        services.AddSingleton(persistence);

        using ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var processor = new ExposedUpscaleTaskProcessor(
            new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>()),
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            persistence,
            new PreprocessedInputCache()
        );

        var recording = new RecordingTask();
        var task = new PersistedTask
        {
            Id = 987655,
            Data = recording,
            Status = PersistedTaskStatus.Processing,
        };

        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        Assert.False(recording.Processed);
        Assert.Equal(PersistedTaskStatus.Pending, task.Status);
        await persistence
            .Received(1)
            .RequeueStrandedTaskAsync(987655, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_WhenReroutedExistenceCheckIsCancelled_AppliesTheCancelInsteadOfStranding()
    {
        // Regression guard: CancelCurrent cannot reach a rerouted task because it is not in
        // runningTasks, and the existence check's OperationCanceledException used to be swallowed.
        // That left the row Processing and out of every queue; the intended cancel must be applied.
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TaskGuardCancel_{Guid.NewGuid()}")
        );
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = false })
        );
        var persistence = Substitute.For<ITaskPersistenceService>();
        persistence
            .CancelTaskAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(1);
        services.AddSingleton(persistence);

        using ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var processor = new ExposedUpscaleTaskProcessor(
            new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>()),
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            persistence,
            new PreprocessedInputCache()
        );

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var recording = new RecordingTask();
        var task = new PersistedTask
        {
            Id = 987656,
            Data = recording,
            Status = PersistedTaskStatus.Processing,
        };

        await processor.InvokeProcessTaskAsync(task, canceled.Token);

        Assert.False(recording.Processed);
        Assert.Equal(PersistedTaskStatus.Canceled, task.Status);
        await persistence.Received(1).CancelTaskAsync(987656, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessTaskAsync_ReroutedTaskWhoseRowIsTerminal_IsSkipped()
    {
        // Regression guard: the reroute existence check only tested the id, so a finalized
        // (e.g. canceled) rerouted row still executed its real side effects before the guarded
        // CompleteTaskAsync affected no row.
        var services = new ServiceCollection();
        string dbName = $"TaskGuardTerminal_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = false })
        );
        services.AddSingleton(Substitute.For<ITaskPersistenceService>());

        using ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        using (var seedScope = scopeFactory.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PersistedTasks.Add(
                new PersistedTask
                {
                    Id = 987657,
                    Data = new DetectSplitCandidatesTask(1, 1),
                    Status = PersistedTaskStatus.Canceled,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var processor = new ExposedUpscaleTaskProcessor(
            new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>()),
            scopeFactory,
            provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            provider.GetRequiredService<ITaskPersistenceService>(),
            new PreprocessedInputCache()
        );

        var recording = new RecordingTask();
        var task = new PersistedTask
        {
            Id = 987657,
            Data = recording,
            Status = PersistedTaskStatus.Processing,
        };

        await processor.InvokeProcessTaskAsync(task, TestContext.Current.CancellationToken);

        Assert.False(recording.Processed);
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

    private sealed class RecordingTask : BaseTask
    {
        public bool Processed { get; private set; }

        public override Task ProcessAsync(
            IServiceProvider services,
            CancellationToken cancellationToken
        )
        {
            Processed = true;
            return Task.CompletedTask;
        }
    }
}
