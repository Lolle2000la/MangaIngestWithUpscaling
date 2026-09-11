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
    public Task ExecuteAsync_WhenClaimIsCancelled_KeepsProcessingSubsequentTasks() =>
        RunClaimFailureScenarioAsync(new OperationCanceledException("canceled"));

    [Fact]
    [Trait("Category", "Unit")]
    public Task ExecuteAsync_WhenClaimThrows_KeepsProcessingSubsequentTasks() =>
        RunClaimFailureScenarioAsync(new InvalidOperationException("claim failed"));

    private async Task RunClaimFailureScenarioAsync(Exception claimFailure)
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

        // The first task's claim failed, so the surviving, processed task must be the second one.
        Assert.Equal("second", ((LoggingTask)processedTask.Data).Message);

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
        Assert.False(IsExecuteTaskFaulted(processor));
    }

    private static bool IsExecuteTaskFaulted(BackgroundService service)
    {
        Task? executeTask = (Task?)
            typeof(BackgroundService)
                .GetField("_executeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service);
        return executeTask?.IsFaulted ?? false;
    }
}
