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

public class DistributedUpscaleTaskProcessorTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITaskPersistenceService _mockPersistence;
    private readonly IOptions<UpscalerConfig> _mockOptions;
    private readonly IServiceScope _scope;
    private readonly TaskQueue _taskQueue;
    private readonly DistributedUpscaleTaskProcessor _processor;

    public DistributedUpscaleTaskProcessorTests()
    {
        var services = new ServiceCollection();
        var dbName = $"TestDb_DistributedProcessor_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        _mockPersistence = Substitute.For<ITaskPersistenceService>();
        _mockPersistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        _mockOptions = Substitute.For<IOptions<UpscalerConfig>>();
        _mockOptions.Value.Returns(new UpscalerConfig { RemoteOnly = true });

        var mockQueueCleanup = Substitute.For<IQueueCleanup>();
        mockQueueCleanup
            .CleanupAsync()
            .Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => mockQueueCleanup);

        var mockLogger = Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>();
        services.AddSingleton(mockLogger);
        services.AddSingleton(_mockPersistence);

        var serviceProvider = services.BuildServiceProvider();
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var queueLogger = Substitute.For<ILogger<TaskQueue>>();

        _taskQueue = new TaskQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            queueLogger
        );

        _processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockOptions,
            _mockPersistence
        );
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTask_ShouldReturnTaskFromSignal()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        // Use DetectSplitCandidatesTask as it's an upscale task but not specifically handled
        // by the DB-check logic in DistributedUpscaleTaskProcessor.ExecuteAsync
        var splitTask = new DetectSplitCandidatesTask(1, 1);
        await _taskQueue.EnqueueAsync(splitTask);

        // Act
        var runTask = _processor.StartAsync(cts.Token);

        // GetTask will write to _taskRequests, which ExecuteAsync will pick up,
        // then it will wait for signal on upscale queue, then dequeue.
        var taskResult = await _processor.GetTask(cts.Token);

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }

        // Assert
        Assert.NotNull(taskResult);
        Assert.IsType<DetectSplitCandidatesTask>(taskResult.Data);
    }
}
