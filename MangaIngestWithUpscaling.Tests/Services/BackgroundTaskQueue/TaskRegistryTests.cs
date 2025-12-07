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

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_GetHashCode_IsConsistentAcrossPropertyChanges()
    {
        // Arrange
        var task = new PersistedTask
        {
            Id = 123,
            Status = PersistedTaskStatus.Pending,
            RetryCount = 0,
            ProcessedAt = null,
        };

        // Act: Get initial hash code
        int initialHashCode = task.GetHashCode();

        // Modify mutable properties
        task.Status = PersistedTaskStatus.Processing;
        task.RetryCount = 5;
        task.ProcessedAt = DateTime.UtcNow;

        int afterChangeHashCode = task.GetHashCode();

        // Assert: Hash code should remain the same (based only on immutable Id)
        Assert.Equal(initialHashCode, afterChangeHashCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_Equals_ComparesById()
    {
        // Arrange
        var task1 = new PersistedTask
        {
            Id = 123,
            Status = PersistedTaskStatus.Pending,
            RetryCount = 0,
        };

        var task2 = new PersistedTask
        {
            Id = 123,
            Status = PersistedTaskStatus.Completed,
            RetryCount = 5,
        };

        var task3 = new PersistedTask { Id = 456, Status = PersistedTaskStatus.Pending };

        // Act & Assert
        Assert.True(task1.Equals(task2)); // Same ID
        Assert.False(task1.Equals(task3)); // Different ID
        Assert.False(task1.Equals(null)); // Null comparison
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistedTask_HashSet_WorksCorrectlyWithMutableProperties()
    {
        // Arrange
        var task = new PersistedTask
        {
            Id = 123,
            Status = PersistedTaskStatus.Pending,
            RetryCount = 0,
        };

        var hashSet = new HashSet<PersistedTask> { task };

        // Act: Modify mutable properties
        task.Status = PersistedTaskStatus.Processing;
        task.RetryCount = 5;

        // Assert: Task should still be found in HashSet
        Assert.Contains(task, hashSet);
        Assert.Single(hashSet);
    }
}
