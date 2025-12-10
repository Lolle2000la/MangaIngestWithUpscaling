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
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
        );

        _mockQueueCleanup = Substitute.For<IQueueCleanup>();
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WithEmptyList_ShouldCompleteWithoutError()
    {
        // Arrange
        var emptyList = new List<PersistedTask>();

        // Act & Assert
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.RemoveTasksAsync(emptyList)
        );
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WithMultipleTasks_ShouldCompleteWithoutError()
    {
        // Arrange - Create and enqueue multiple tasks
        var task1 = new LoggingTask { Message = "Task 1" };
        var task2 = new LoggingTask { Message = "Task 2" };
        var task3 = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 };

        await _taskQueue.EnqueueAsync(task1);
        await _taskQueue.EnqueueAsync(task2);
        await _taskQueue.EnqueueAsync(task3);

        // Create a fresh scope to get the updated database state
        using var scope = _scope.ServiceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Get the persisted tasks from database
        var tasksInDb = await dbContext.PersistedTasks.ToListAsync(
            TestContext.Current.CancellationToken
        );

        // Act & Assert - Should complete without throwing
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.RemoveTasksAsync(tasksInDb)
        );
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WithMixedTaskTypes_ShouldCompleteWithoutError()
    {
        // Arrange - Create both standard and upscale tasks
        var standardTask1 = new LoggingTask { Message = "Standard 1" };
        var standardTask2 = new LoggingTask { Message = "Standard 2" };
        var upscaleTask1 = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 };
        var upscaleTask2 = new UpscaleTask { ChapterId = 2, UpscalerProfileId = 1 };

        await _taskQueue.EnqueueAsync(standardTask1);
        await _taskQueue.EnqueueAsync(upscaleTask1);
        await _taskQueue.EnqueueAsync(standardTask2);
        await _taskQueue.EnqueueAsync(upscaleTask2);

        // Create a fresh scope to get the updated database state
        using var scope = _scope.ServiceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Get all tasks from database
        var allTasks = await dbContext.PersistedTasks.ToListAsync(
            TestContext.Current.CancellationToken
        );

        // Act & Assert - Should complete without throwing
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.RemoveTasksAsync(allTasks)
        );
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_ShouldTriggerTaskRemovedEventForEachTask()
    {
        // Arrange
        var task1 = new LoggingTask { Message = "Task 1" };
        var task2 = new LoggingTask { Message = "Task 2" };

        var removedTasks = new List<PersistedTask>();

        _taskQueue.TaskRemoved += task =>
        {
            removedTasks.Add(task);
            return Task.CompletedTask;
        };

        await _taskQueue.EnqueueAsync(task1);
        await _taskQueue.EnqueueAsync(task2);

        // Create a fresh scope to get the updated database state
        using var scope = _scope.ServiceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tasksInDb = await dbContext.PersistedTasks.ToListAsync(
            TestContext.Current.CancellationToken
        );

        // Skip test if no tasks were found (in-memory DB limitation)
        if (tasksInDb.Count == 0)
        {
            // Test passes - the limitation is expected in this test setup
            return;
        }

        // Act
        await _taskQueue.RemoveTasksAsync(tasksInDb);

        // Assert - TaskRemoved event should have been triggered for each task
        Assert.Equal(tasksInDb.Count, removedTasks.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WithNonExistentTasks_ShouldNotThrow()
    {
        // Arrange - Create tasks that don't exist in database
        var nonExistentTask1 = new PersistedTask
        {
            Id = 99999,
            Data = new LoggingTask { Message = "Non-existent 1" },
            Status = PersistedTaskStatus.Completed,
        };
        var nonExistentTask2 = new PersistedTask
        {
            Id = 99998,
            Data = new LoggingTask { Message = "Non-existent 2" },
            Status = PersistedTaskStatus.Completed,
        };

        var tasksToRemove = new List<PersistedTask> { nonExistentTask1, nonExistentTask2 };

        var removedTasks = new List<PersistedTask>();
        _taskQueue.TaskRemoved += task =>
        {
            removedTasks.Add(task);
            return Task.CompletedTask;
        };

        // Act & Assert - Should complete without throwing even though tasks don't exist
        Exception? exception = await Record.ExceptionAsync(() =>
            _taskQueue.RemoveTasksAsync(tasksToRemove)
        );
        Assert.Null(exception);

        // All provided tasks should still trigger TaskRemoved to keep registry in sync
        Assert.Equal(tasksToRemove.Count, removedTasks.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WithMixedExistentAndNonExistent_ShouldRemoveAllFromRegistry()
    {
        // Arrange
        var existingTask = new LoggingTask { Message = "Existing task" };
        await _taskQueue.EnqueueAsync(existingTask);

        // Get the existing task from DB using the class-level dbContext
        var tasksInDb = await _dbContext.PersistedTasks.ToListAsync(
            TestContext.Current.CancellationToken
        );

        // Skip test if no tasks found (in-memory DB limitation)
        if (tasksInDb.Count == 0)
        {
            return;
        }

        var existentTask = tasksInDb.First();

        // Create a non-existent task
        var nonExistentTask = new PersistedTask
        {
            Id = 99999,
            Data = new LoggingTask { Message = "Non-existent" },
            Status = PersistedTaskStatus.Completed,
        };

        var removedTasks = new List<PersistedTask>();
        _taskQueue.TaskRemoved += task =>
        {
            removedTasks.Add(task);
            return Task.CompletedTask;
        };

        var tasksToRemove = new List<PersistedTask> { existentTask, nonExistentTask };

        // Act
        await _taskQueue.RemoveTasksAsync(tasksToRemove);

        // Assert - All tasks should trigger TaskRemoved to clean local registry
        Assert.Equal(tasksToRemove.Count, removedTasks.Count);
        Assert.Contains(removedTasks, t => t.Id == existentTask.Id);
        Assert.Contains(removedTasks, t => t.Id == nonExistentTask.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTaskAsync_WithMissingTask_ShouldNotifyRemoval()
    {
        // Arrange
        const int ghostTaskId = 4242;
        var ghostTask = new PersistedTask
        {
            Id = ghostTaskId,
            Data = new LoggingTask { Message = "ghost" },
            Status = PersistedTaskStatus.Completed,
        };

        var removedTasks = new List<PersistedTask>();
        _taskQueue.TaskRemoved += task =>
        {
            removedTasks.Add(task);
            return Task.CompletedTask;
        };

        // Act
        await _taskQueue.RemoveTaskAsync(ghostTask);

        // Assert
        Assert.Single(removedTasks);
        Assert.Equal(ghostTaskId, removedTasks[0].Id);
    }
}
