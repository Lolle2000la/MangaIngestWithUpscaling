using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class TaskQueueTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<TaskQueue> _mockLogger;
    private readonly IQueueCleanup _mockQueueCleanup;
    private readonly IServiceScope _scope;
    private readonly TaskQueue _taskQueue;

    public TaskQueueTests()
    {
        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        _mockQueueCleanup = Substitute.For<IQueueCleanup>();
        _mockQueueCleanup
            .CleanupAsync()
            .Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => _mockQueueCleanup);

        var serviceProvider = services.BuildServiceProvider();
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _mockLogger = Substitute.For<ILogger<TaskQueue>>();

        _taskQueue = new TaskQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger
        );
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendToLocalUpscaleAsync_WithValidTask_ShouldCompleteSuccessfully()
    {
        // Arrange
        var task = new PersistedTask
        {
            Id = 1,
            Order = 1,
            Status = PersistedTaskStatus.Pending,
            Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
        };

        // Act & Assert
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.SendToLocalUpscaleAsync(task, CancellationToken.None).AsTask()
        );
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_WithUpscaleTask_ShouldCallCleanupAsync()
    {
        // Arrange
        var upscaleTask = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 };

        // Act
        await _taskQueue.EnqueueAsync(upscaleTask);

        // Assert - Cleanup should be called when enqueueing tasks
        await _mockQueueCleanup.Received(1).CleanupAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_WithLogTask_ShouldCompleteSuccessfully()
    {
        // Arrange
        var logTask = new LoggingTask { Message = "Test log message" };

        // Act & Assert
        Exception? exception = await Record.ExceptionAsync(() => _taskQueue.EnqueueAsync(logTask));
        Assert.Null(exception);

        // Verify cleanup was called
        await _mockQueueCleanup.Received(1).CleanupAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayPendingOrFailed_ShouldCompleteSuccessfully()
    {
        // Act & Assert - This method handles database operations, should not throw
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.ReplayPendingOrFailed(CancellationToken.None)
        );
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_WhenCleanupRemovesTasks_RaisesTaskRemoved()
    {
        // Arrange
        _mockQueueCleanup
            .CleanupAsync()
            .Returns(Task.FromResult<IReadOnlyList<int>>(new List<int> { 5 }));

        var removalNotified = new TaskCompletionSource<int>();
        _taskQueue.TaskRemoved += task =>
        {
            removalNotified.TrySetResult(task.Id);
            return Task.CompletedTask;
        };

        // Act
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "cleanup" });

        // Assert
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        var removedId = await removalNotified.Task.WaitAsync(cts.Token);
        Assert.Equal(5, removedId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChannelReaders_ShouldProvideAccessToTaskChannels()
    {
        // Act & Assert
        Assert.NotNull(_taskQueue.StandardReader);
        Assert.NotNull(_taskQueue.UpscaleReader);
        Assert.NotNull(_taskQueue.ReroutedUpscaleReader);

        // Channel readers should be distinct objects
        Assert.NotSame(_taskQueue.StandardReader, _taskQueue.UpscaleReader);
        Assert.NotSame(_taskQueue.StandardReader, _taskQueue.ReroutedUpscaleReader);
        Assert.NotSame(_taskQueue.UpscaleReader, _taskQueue.ReroutedUpscaleReader);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartAsync_ShouldCompleteSuccessfully()
    {
        // Act & Assert - StartAsync calls ReplayPendingOrFailed and should complete
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.StartAsync(CancellationToken.None)
        );
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetSnapshots_ShouldReturnReadOnlyCollections()
    {
        // Act
        var standardSnapshot = _taskQueue.GetStandardSnapshot();
        var upscaleSnapshot = _taskQueue.GetUpscaleSnapshot();

        // Assert
        Assert.NotNull(standardSnapshot);
        Assert.NotNull(upscaleSnapshot);
        Assert.IsAssignableFrom<IReadOnlyList<PersistedTask>>(standardSnapshot);
        Assert.IsAssignableFrom<IReadOnlyList<PersistedTask>>(upscaleSnapshot);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StopAsync_WithCancellation_ShouldHandleGracefully()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        Exception? exception = await Record.ExceptionAsync(() => _taskQueue.StopAsync(cts.Token));

        // Should handle cancellation gracefully without throwing
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetryAsync_WithNonExistentTask_ShouldThrowDbUpdateConcurrencyException()
    {
        // Arrange - Create a task that doesn't exist in the database
        var task = new PersistedTask
        {
            Id = 999, // Non-existent ID
            Order = 1,
            Status = PersistedTaskStatus.Failed,
            Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
        };

        // Act & Assert - Should throw exception when trying to update non-existent entity
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _taskQueue.RetryAsync(task));
    }
}
