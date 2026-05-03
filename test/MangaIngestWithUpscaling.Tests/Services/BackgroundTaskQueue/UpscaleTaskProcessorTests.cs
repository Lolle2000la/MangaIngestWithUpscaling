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
        _mockPersistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

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

        var upscaleTask = new PersistedTask
        {
            Id = 1,
            Order = 10,
            Status = PersistedTaskStatus.Pending,
            Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
        };

        var reroutedTask = new PersistedTask
        {
            Id = 2,
            Order = 100, // Much lower priority by order
            Status = PersistedTaskStatus.Pending,
            Data = new RepairUpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
        };

        // Enqueue upscale task (will emit signal)
        await _taskQueue.EnqueueAsync((UpscaleTask)upscaleTask.Data);
        // Send rerouted task
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
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();

        // Assert
        Assert.NotNull(firstProcessed);
        Assert.Equal(reroutedTask.Id, firstProcessed.Id); // Rerouted should come first regardless of order
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ShouldProcessMainQueueWhenNoReroutedTasks()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var upscaleTask = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 };
        await _taskQueue.EnqueueAsync(upscaleTask);

        var tcs = new TaskCompletionSource<PersistedTask>();
        _processor.StatusChanged += (task) =>
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                tcs.TrySetResult(task);
            }
            return Task.CompletedTask;
        };

        // Act
        var runTask = _processor.StartAsync(cts.Token);
        var processed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();

        // Assert
        Assert.NotNull(processed);
        Assert.IsType<UpscaleTask>(processed.Data);
    }
}
