using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class TaskRegistryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_CompletedReturnsLowestPriority()
    {
        // Arrange
        var completedTask = new PersistedTask { Status = PersistedTaskStatus.Completed };

        // Act
        int priority = completedTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_CanceledReturnsLowestPriority()
    {
        // Arrange
        var canceledTask = new PersistedTask { Status = PersistedTaskStatus.Canceled };

        // Act
        int priority = canceledTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_ProcessingReturnsSecondPriority()
    {
        // Arrange
        var processingTask = new PersistedTask { Status = PersistedTaskStatus.Processing };

        // Act
        int priority = processingTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(1, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_PendingReturnsThirdPriority()
    {
        // Arrange
        var pendingTask = new PersistedTask { Status = PersistedTaskStatus.Pending };

        // Act
        int priority = pendingTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(2, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_FailedReturnsLowestPriority()
    {
        // Arrange
        var failedTask = new PersistedTask { Status = PersistedTaskStatus.Failed };

        // Act
        int priority = failedTask.GetStatusSortPriority();

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetStatusSortPriority_SortOrderIsCorrect()
    {
        // Arrange
        var tasks = new List<PersistedTask>
        {
            new()
            {
                Id = 1,
                Status = PersistedTaskStatus.Failed,
                Order = 1,
            },
            new()
            {
                Id = 2,
                Status = PersistedTaskStatus.Pending,
                Order = 2,
            },
            new()
            {
                Id = 3,
                Status = PersistedTaskStatus.Processing,
                Order = 3,
            },
            new()
            {
                Id = 4,
                Status = PersistedTaskStatus.Completed,
                Order = 4,
            },
            new()
            {
                Id = 5,
                Status = PersistedTaskStatus.Canceled,
                Order = 5,
            },
        };

        // Act: Sort by status priority, then by order
        var sortedTasks = tasks
            .OrderBy(t => t.GetStatusSortPriority())
            .ThenBy(t => t.Order)
            .ToList();

        // Assert: Order should be Failed, Completed, Canceled, Processing, Pending
        Assert.Equal(1, sortedTasks[0].Id); // Failed
        Assert.Equal(4, sortedTasks[1].Id); // Completed
        Assert.Equal(5, sortedTasks[2].Id); // Canceled
        Assert.Equal(3, sortedTasks[3].Id); // Processing
        Assert.Equal(2, sortedTasks[4].Id); // Pending
    }
}
