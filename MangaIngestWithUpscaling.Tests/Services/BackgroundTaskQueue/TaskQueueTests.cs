using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class TaskQueueTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTaskAsync_TaskMissingInDb_NotifiesRemoval()
    {
        // Arrange
        (TaskQueue queue, List<int> removedIds) = CreateQueueWithTracking();
        var ghostTask = new PersistedTask
        {
            Id = 123,
            Data = new LoggingTask { Message = "ghost" },
            Status = PersistedTaskStatus.Completed,
        };

        // Act
        await queue.RemoveTaskAsync(ghostTask);

        // Assert
        Assert.Single(removedIds);
        Assert.Contains(ghostTask.Id, removedIds);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_TaskMissingInDb_NotifiesRemoval()
    {
        // Arrange
        (TaskQueue queue, List<int> removedIds) = CreateQueueWithTracking();
        var ghostTasks = new List<PersistedTask>
        {
            new()
            {
                Id = 1,
                Data = new LoggingTask { Message = "ghost1" },
                Status = PersistedTaskStatus.Completed,
            },
            new()
            {
                Id = 2,
                Data = new LoggingTask { Message = "ghost2" },
                Status = PersistedTaskStatus.Completed,
            },
        };

        // Act
        await queue.RemoveTasksAsync(ghostTasks);

        // Assert
        Assert.Equal(2, removedIds.Count);
        Assert.Contains(1, removedIds);
        Assert.Contains(2, removedIds);
    }

    private static (TaskQueue queue, List<int> removedIds) CreateQueueWithTracking()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString())
        );

        IServiceProvider provider = services.BuildServiceProvider();

        var queue = new TaskQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<TaskQueue>>()
        );

        var removedIds = new List<int>();
        queue.TaskRemoved += task =>
        {
            removedIds.Add(task.Id);
            return Task.CompletedTask;
        };

        return (queue, removedIds);
    }
}
