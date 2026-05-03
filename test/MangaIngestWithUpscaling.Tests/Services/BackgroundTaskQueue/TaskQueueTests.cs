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
            _taskQueue.SendToLocalUpscaleAsync(task, TestContext.Current.CancellationToken).AsTask()
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
            _taskQueue.ReplayPendingOrFailed(TestContext.Current.CancellationToken)
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
            _taskQueue.StartAsync(TestContext.Current.CancellationToken)
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_ShouldEmitSignal()
    {
        // Arrange
        var logTask = new LoggingTask { Message = "test" };

        // Act
        await _taskQueue.EnqueueAsync(logTask);

        // Assert
        bool hasSignal = _taskQueue.StandardReader.TryRead(out _);
        Assert.True(hasSignal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DequeueStandard_ShouldReturnHighestPriorityTask()
    {
        // Arrange
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "task2" }); // Order 1
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "task1" }); // Order 2

        var snapshot = _taskQueue.GetStandardSnapshot();
        var task1 = snapshot.First(t => ((LoggingTask)t.Data).Message == "task1");
        var task2 = snapshot.First(t => ((LoggingTask)t.Data).Message == "task2");

        // Force task1 to have higher priority (lower order)
        await _taskQueue.ReorderTaskAsync(task1, 0);

        // Act
        var dequeued = _taskQueue.DequeueStandard();

        // Assert
        Assert.NotNull(dequeued);
        Assert.Equal("task1", ((LoggingTask)dequeued.Data).Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_DetectSplitCandidatesTask_ShouldRouteToUpscale()
    {
        // Arrange
        var splitTask = new DetectSplitCandidatesTask(1, 1);

        // Act
        await _taskQueue.EnqueueAsync(splitTask);

        // Assert
        Assert.Single(_taskQueue.GetUpscaleSnapshot());
        Assert.Empty(_taskQueue.GetStandardSnapshot());
        Assert.True(_taskQueue.UpscaleReader.TryRead(out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReorderTaskAsync_DetectSplitCandidatesTask_ShouldUpdateUpscaleSet()
    {
        // Arrange
        var splitTaskData = new DetectSplitCandidatesTask(1, 1);
        await _taskQueue.EnqueueAsync(splitTaskData);
        var task = _taskQueue.GetUpscaleSnapshot().First();

        // Act
        await _taskQueue.ReorderTaskAsync(task, -10);

        // Assert
        var dequeued = _taskQueue.DequeueUpscale();
        Assert.NotNull(dequeued);
        Assert.Equal(-10, dequeued.Order);
        Assert.IsType<DetectSplitCandidatesTask>(dequeued.Data);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTaskAsync_ApplySplitsTask_ShouldRemoveFromUpscaleSet()
    {
        // Arrange
        var splitTaskData = new ApplySplitsTask(1, 1);
        await _taskQueue.EnqueueAsync(splitTaskData);
        var task = _taskQueue.GetUpscaleSnapshot().First();

        // Act
        await _taskQueue.RemoveTaskAsync(task);

        // Assert
        Assert.Empty(_taskQueue.GetUpscaleSnapshot());
        Assert.Null(_taskQueue.DequeueUpscale());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayPendingOrFailed_WithFailedTasks_ShouldResetToPending()
    {
        // Arrange
        var failedTask = new PersistedTask
        {
            Id = 11,
            Order = 1,
            Status = PersistedTaskStatus.Failed,
            RetryCount = 0,
            Data = new LoggingTask { Message = "failed", RetryFor = 1 },
        };
        _dbContext.PersistedTasks.Add(failedTask);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _taskQueue.ReplayPendingOrFailed(TestContext.Current.CancellationToken);

        // Assert
        // Use a separate scope to verify DB changes
        using var checkScope = _scope.ServiceProvider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reloaded = await checkDb
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == 11, TestContext.Current.CancellationToken);

        Assert.NotNull(reloaded);
        Assert.Equal(PersistedTaskStatus.Pending, reloaded.Status);
        Assert.True(_taskQueue.StandardReader.TryRead(out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayPendingOrFailed_WithMultiplePendingTasks_ShouldSignalAll()
    {
        // Arrange
        var tasks = new[]
        {
            new PersistedTask
            {
                Id = 101,
                Order = 1,
                Status = PersistedTaskStatus.Pending,
                Data = new LoggingTask { Message = "1" },
            },
            new PersistedTask
            {
                Id = 102,
                Order = 2,
                Status = PersistedTaskStatus.Pending,
                Data = new LoggingTask { Message = "2" },
            },
        };
        _dbContext.PersistedTasks.AddRange(tasks);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _taskQueue.ReplayPendingOrFailed(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(_taskQueue.StandardReader.TryRead(out _), "First signal should be present");
        Assert.True(_taskQueue.StandardReader.TryRead(out _), "Second signal should be present");
        Assert.False(
            _taskQueue.StandardReader.TryRead(out _),
            "Third signal should NOT be present"
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetryAsync_ShouldEmitSignal()
    {
        // Arrange
        var task = new PersistedTask
        {
            Id = 301,
            Order = 1,
            Status = PersistedTaskStatus.Failed,
            Data = new LoggingTask { Message = "retry-signal" },
        };
        _dbContext.PersistedTasks.Add(task);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _taskQueue.RetryAsync(task);

        // Assert
        Assert.True(
            _taskQueue.StandardReader.TryRead(out _),
            "RetryAsync should emit a signal for standard tasks"
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DequeueStandard_WhenEmptyWithStaleSignal_ShouldReturnNull()
    {
        // Arrange - Enqueue and then remove to leave a stale signal
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "stale" });
        var task = _taskQueue.GetStandardSnapshot().First();
        await _taskQueue.RemoveTaskAsync(task);

        // Assert
        Assert.True(_taskQueue.StandardReader.TryRead(out _), "Stale signal should be present");
        Assert.Null(_taskQueue.DequeueStandard());
    }
}
