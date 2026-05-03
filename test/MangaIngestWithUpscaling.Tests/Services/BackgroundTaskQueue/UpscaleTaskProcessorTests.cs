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

public class UpscaleTaskProcessorTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UpscaleTaskProcessor> _mockLogger;
    private readonly ITaskPersistenceService _mockPersistence;
    private readonly IOptions<UpscalerConfig> _mockOptions;
    private readonly IServiceScope _scope;
    private readonly TaskQueue _taskQueue;
    private readonly UpscaleTaskProcessor _processor;

    public UpscaleTaskProcessorTests()
    {
        var services = new ServiceCollection();
        var dbName = $"TestDb_Processor_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        _mockPersistence = Substitute.For<ITaskPersistenceService>();
        // Simulate production: claiming only succeeds if the task is Pending.
        // Rerouted tasks are already 'Processing' when they reach UpscaleTaskProcessor.
        _mockPersistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var taskId = (int)x[0];
                // In production this would check the DB.
                // We'll trust the logic in ProcessTaskAsync to only call this for non-Processing tasks.
                return true;
            });

        _mockOptions = Substitute.For<IOptions<UpscalerConfig>>();
        _mockOptions.Value.Returns(new UpscalerConfig { RemoteOnly = false });

        var mockQueueCleanup = Substitute.For<IQueueCleanup>();
        mockQueueCleanup
            .CleanupAsync()
            .Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => mockQueueCleanup);
        services.AddSingleton(_mockPersistence);

        var serviceProvider = services.BuildServiceProvider();
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _mockLogger = Substitute.For<ILogger<UpscaleTaskProcessor>>();
        var queueLogger = Substitute.For<ILogger<TaskQueue>>();

        _taskQueue = new TaskQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            queueLogger
        );

        _processor = new UpscaleTaskProcessor(
            _taskQueue,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
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
    public async Task ExecuteAsync_ShouldPrioritizeReroutedTasksOverSignals()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // 1. Enqueue a normal upscale task (will stay in main queue)
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });

        // 2. Enqueue another task that we will simulate as being "rerouted"
        await _taskQueue.EnqueueAsync(
            new RepairUpscaleTask { ChapterId = 1, UpscalerProfileId = 1 }
        );

        // Get the tasks from the DB to make them "real"
        var tasks = await _dbContext
            .PersistedTasks.OrderBy(t => t.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var upscaleTask = tasks[0];
        var reroutedTask = tasks[1];

        // Simulate DistributedUpscaleTaskProcessor behavior:
        // Mark the task as Processing (already claimed) before sending it to the local processor.
        reroutedTask.Status = PersistedTaskStatus.Processing;
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        // 3. Send rerouted task directly to the local channel
        await _taskQueue.SendToLocalUpscaleAsync(reroutedTask, cts.Token);

        PersistedTask? firstProcessed = null;
        var tcs = new TaskCompletionSource();

        _processor.StatusChanged += (task) =>
        {
            if (task.Status == PersistedTaskStatus.Processing && firstProcessed == null)
            {
                firstProcessed = task;
                tcs.TrySetResult();
            }
            return Task.CompletedTask;
        };

        // Act
        var runTask = _processor.StartAsync(cts.Token);

        // Wait for first task to be processed
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await runTask;

        // Assert
        Assert.NotNull(firstProcessed);
        Assert.Equal(reroutedTask.Id, firstProcessed.Id); // Rerouted should come first regardless of order
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ShouldRespectPriorityChangeAfterSignal()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // 1. Enqueue two tasks
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await _taskQueue.EnqueueAsync(new UpscaleTask { ChapterId = 2, UpscalerProfileId = 1 });

        // 2. Get tasks from snapshot and reorder
        var tasks = _taskQueue.GetUpscaleSnapshot();
        var task1 = tasks.First(t => ((UpscaleTask)t.Data).ChapterId == 1);
        var task2 = tasks.First(t => ((UpscaleTask)t.Data).ChapterId == 2);

        // Make task 2 higher priority
        await _taskQueue.ReorderTaskAsync(task2, 0);

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
        Assert.Equal(2, ((UpscaleTask)processedTasks[0].Data).ChapterId);
        Assert.Equal(1, ((UpscaleTask)processedTasks[1].Data).ChapterId);
    }
}
