using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class QueueCleanupTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<QueueCleanup> _mockLogger;
    private readonly ITaskQueue _mockTaskQueue;
    private readonly QueueCleanup _queueCleanup;
    private readonly IServiceScope _scope;

    public QueueCleanupTests()
    {
        // Setup in-memory database
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
        );

        var serviceProvider = services.BuildServiceProvider();
        _scope = serviceProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _mockLogger = Substitute.For<ILogger<QueueCleanup>>();
        _mockTaskQueue = Substitute.For<ITaskQueue>();

        _queueCleanup = new QueueCleanup(_dbContext, _mockTaskQueue, _mockLogger);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CleanupAsync_WithNoTasks_ShouldNotCallRemoveTasksAsync()
    {
        // Act
        await _queueCleanup.CleanupAsync();

        // Assert - Should not call RemoveTasksAsync when there are no tasks to cleanup
        await _mockTaskQueue
            .DidNotReceive()
            .RemoveTasksAsync(Arg.Any<IEnumerable<PersistedTask>>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CleanupAsync_WithFewCompletedTasks_ShouldNotCallRemoveTasksAsync()
    {
        // Arrange - Add 50 completed tasks (below the 100+25 threshold)
        for (int i = 0; i < 50; i++)
        {
            _dbContext.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = new LoggingTask { Message = $"Task {i}" },
                    Status = PersistedTaskStatus.Completed,
                    CreatedAt = DateTime.UtcNow.AddHours(-i),
                }
            );
        }
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _queueCleanup.CleanupAsync();

        // Assert - Should not cleanup when below threshold
        await _mockTaskQueue
            .DidNotReceive()
            .RemoveTasksAsync(Arg.Any<IEnumerable<PersistedTask>>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CleanupAsync_WithManyCompletedTasks_ShouldCallRemoveTasksAsync()
    {
        // Arrange - Add 150 completed tasks (above the 100+25 threshold)
        for (int i = 0; i < 150; i++)
        {
            _dbContext.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = new LoggingTask { Message = $"Task {i}" },
                    Status = PersistedTaskStatus.Completed,
                    CreatedAt = DateTime.UtcNow.AddHours(-i), // Older tasks have earlier timestamps
                }
            );
        }
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _queueCleanup.CleanupAsync();

        // Assert - Should call RemoveTasksAsync with the oldest 50 tasks (150 - 100)
        await _mockTaskQueue
            .Received(1)
            .RemoveTasksAsync(Arg.Is<IEnumerable<PersistedTask>>(tasks => tasks.Count() == 50));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CleanupAsync_ShouldOnlyRemoveCompletedTasks()
    {
        // Arrange - Add 200 completed tasks and 100 pending tasks
        for (int i = 0; i < 200; i++)
        {
            _dbContext.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = new LoggingTask { Message = $"Completed Task {i}" },
                    Status = PersistedTaskStatus.Completed,
                    CreatedAt = DateTime.UtcNow.AddHours(-i),
                }
            );
        }

        for (int i = 0; i < 100; i++)
        {
            _dbContext.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = new LoggingTask { Message = $"Pending Task {i}" },
                    Status = PersistedTaskStatus.Pending,
                    CreatedAt = DateTime.UtcNow.AddHours(-i),
                }
            );
        }
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await _queueCleanup.CleanupAsync();

        // Assert - Should only remove completed tasks (100 oldest of 200 completed)
        await _mockTaskQueue
            .Received(1)
            .RemoveTasksAsync(
                Arg.Is<IEnumerable<PersistedTask>>(tasks =>
                    tasks.Count() == 100
                    && tasks.All(t => t.Status == PersistedTaskStatus.Completed)
                )
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CleanupAsync_ShouldKeepMostRecent100CompletedTasks()
    {
        // Arrange - Add 150 completed tasks with known creation times
        var now = DateTime.UtcNow;
        var allTasks = new List<PersistedTask>();

        for (int i = 0; i < 150; i++)
        {
            var task = new PersistedTask
            {
                Data = new LoggingTask { Message = $"Task {i}" },
                Status = PersistedTaskStatus.Completed,
                CreatedAt = now.AddHours(-i), // Task 0 is newest, Task 149 is oldest
            };
            _dbContext.PersistedTasks.Add(task);
            allTasks.Add(task);
        }
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        IEnumerable<PersistedTask>? capturedTasks = null;
        await _mockTaskQueue.RemoveTasksAsync(
            Arg.Do<IEnumerable<PersistedTask>>(t => capturedTasks = t.ToList())
        );

        // Act
        await _queueCleanup.CleanupAsync();

        // Assert - Should remove exactly 50 tasks
        Assert.NotNull(capturedTasks);
        Assert.Equal(50, capturedTasks.Count());

        // All removed tasks should be older than the 100th most recent task
        var oldestKeptTime = now.AddHours(-99);
        Assert.All(capturedTasks, t => Assert.True(t.CreatedAt < oldestKeptTime));
    }
}
