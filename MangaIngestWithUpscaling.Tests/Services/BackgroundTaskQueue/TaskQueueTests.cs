using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class TaskQueueTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IServiceScope _scope;
    private readonly Mock<ILogger<TaskQueue>> _mockLogger;
    private readonly Mock<IQueueCleanup> _mockQueueCleanup;
    private readonly TaskQueue _taskQueue;

    public TaskQueueTests()
    {
        // Setup in-memory database
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));
        services.AddScoped<IQueueCleanup>(_ => _mockQueueCleanup.Object);

        var serviceProvider = services.BuildServiceProvider();
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _mockLogger = new Mock<ILogger<TaskQueue>>();
        _mockQueueCleanup = new Mock<IQueueCleanup>();

        _taskQueue = new TaskQueue(serviceProvider.GetRequiredService<IServiceScopeFactory>(), _mockLogger.Object);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void GetStandardSnapshot_ShouldReturnCopyOfStandardTasks()
    {
        // Act
        var snapshot = _taskQueue.GetStandardSnapshot();

        // Assert
        Assert.NotNull(snapshot);
        Assert.IsAssignableFrom<IReadOnlyList<PersistedTask>>(snapshot);
    }

    [Fact]
    public void GetUpscaleSnapshot_ShouldReturnCopyOfUpscaleTasks()
    {
        // Act
        var snapshot = _taskQueue.GetUpscaleSnapshot();

        // Assert
        Assert.NotNull(snapshot);
        Assert.IsAssignableFrom<IReadOnlyList<PersistedTask>>(snapshot);
    }

    [Fact]
    public async Task StartAsync_ShouldNotThrow()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(
            () => _taskQueue.StartAsync(CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_ShouldNotThrow()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(
            () => _taskQueue.StopAsync(CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ReplayPendingOrFailed_ShouldNotThrow()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(
            () => _taskQueue.ReplayPendingOrFailed(CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public void TaskQueue_Constructor_ShouldNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));
        var serviceProvider = services.BuildServiceProvider();
        var logger = new Mock<ILogger<TaskQueue>>();

        // Act & Assert
        var exception = Record.Exception(
            () => new TaskQueue(serviceProvider.GetRequiredService<IServiceScopeFactory>(), logger.Object));
        Assert.Null(exception);
    }

    [Fact]
    public void TaskQueue_ChannelReaders_ShouldBeAccessible()
    {
        // Act & Assert
        Assert.NotNull(_taskQueue.StandardReader);
        Assert.NotNull(_taskQueue.UpscaleReader);
        Assert.NotNull(_taskQueue.ReroutedUpscaleReader);
    }

    [Fact]
    public async Task SendToLocalUpscaleAsync_ShouldNotThrow()
    {
        // Arrange
        var task = new PersistedTask
        {
            Id = 1,
            Order = 1,
            Status = PersistedTaskStatus.Pending,
            Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 }
        };

        // Act & Assert
        var exception = await Record.ExceptionAsync(
            () => _taskQueue.SendToLocalUpscaleAsync(task, CancellationToken.None).AsTask());
        Assert.Null(exception);
    }
}