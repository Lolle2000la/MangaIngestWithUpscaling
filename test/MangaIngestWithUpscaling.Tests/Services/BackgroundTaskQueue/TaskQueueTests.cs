using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_ShouldInsertInLogicalChapterOrder()
    {
        // Arrange
        var library = new Library
        {
            Name = "TestLib",
            NotUpscaledLibraryPath = "not_upscaled",
            UpscaledLibraryPath = "upscaled",
        };
        var upscalerProfile = new UpscalerProfile
        {
            Name = "TestProfile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.Libraries.Add(library);
        _dbContext.UpscalerProfiles.Add(upscalerProfile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "TestManga",
            LibraryId = library.Id,
            Library = library,
            UpscalerProfilePreference = upscalerProfile,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter2 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 2.cbz",
            RelativePath = "ch2.cbz",
        };
        var chapter1 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 1.cbz",
            RelativePath = "ch1.cbz",
        };
        var chapter3 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 3.cbz",
            RelativePath = "ch3.cbz",
        };
        _dbContext.Chapters.AddRange(chapter1, chapter2, chapter3);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var taskForCh2 = new UpscaleTask(chapter2, upscalerProfile);
        var taskForCh1 = new UpscaleTask(chapter1, upscalerProfile);
        var taskForCh3 = new UpscaleTask(chapter3, upscalerProfile);

        // Act
        // 1. Enqueue Chapter 2 first
        await _taskQueue.EnqueueAsync(taskForCh2);

        // 2. Enqueue Chapter 1 second (should be sorted before Chapter 2)
        await _taskQueue.EnqueueAsync(taskForCh1);

        // 3. Enqueue Chapter 3 third (should be sorted after Chapter 2)
        await _taskQueue.EnqueueAsync(taskForCh3);

        // Assert
        var snapshot = _taskQueue.GetUpscaleSnapshot();
        Assert.Equal(3, snapshot.Count);

        var firstTask = snapshot[0];
        var secondTask = snapshot[1];
        var thirdTask = snapshot[2];

        Assert.Equal(chapter1.Id, ((UpscaleTask)firstTask.Data).ChapterId);
        Assert.Equal(chapter2.Id, ((UpscaleTask)secondTask.Data).ChapterId);
        Assert.Equal(chapter3.Id, ((UpscaleTask)thirdTask.Data).ChapterId);

        // Verify they are dequeued in the correct order
        var dequeued1 = _taskQueue.DequeueUpscale();
        var dequeued2 = _taskQueue.DequeueUpscale();
        var dequeued3 = _taskQueue.DequeueUpscale();

        Assert.NotNull(dequeued1);
        Assert.NotNull(dequeued2);
        Assert.NotNull(dequeued3);

        Assert.Equal(chapter1.Id, ((UpscaleTask)dequeued1.Data).ChapterId);
        Assert.Equal(chapter2.Id, ((UpscaleTask)dequeued2.Data).ChapterId);
        Assert.Equal(chapter3.Id, ((UpscaleTask)dequeued3.Data).ChapterId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnqueueAsync_ShouldRespectFirstOpportunity_WhenQueueIsAlreadyMixed()
    {
        // Arrange
        var library = new Library
        {
            Name = "TestLib2",
            NotUpscaledLibraryPath = "not_upscaled2",
            UpscaledLibraryPath = "upscaled2",
        };
        var upscalerProfile = new UpscalerProfile
        {
            Name = "TestProfile2",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.Libraries.Add(library);
        _dbContext.UpscalerProfiles.Add(upscalerProfile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "TestManga2",
            LibraryId = library.Id,
            Library = library,
            UpscalerProfilePreference = upscalerProfile,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter2 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 2.cbz",
            RelativePath = "ch2.cbz",
        };
        var chapter1 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 1.cbz",
            RelativePath = "ch1.cbz",
        };
        var chapter1_5 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 1.5.cbz",
            RelativePath = "ch1_5.cbz",
        };
        var chapter3 = new Chapter
        {
            MangaId = manga.Id,
            Manga = manga,
            FileName = "Chapter 3.cbz",
            RelativePath = "ch3.cbz",
        };
        _dbContext.Chapters.AddRange(chapter1, chapter2, chapter1_5, chapter3);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // We manually insert Chapter 2 (Order = 10) and Chapter 1 (Order = 20) in the DB
        var dbTaskForCh2 = new PersistedTask
        {
            Data = new UpscaleTask(chapter2, upscalerProfile),
            Order = 10,
            Status = PersistedTaskStatus.Pending,
        };
        var dbTaskForCh1 = new PersistedTask
        {
            Data = new UpscaleTask(chapter1, upscalerProfile),
            Order = 20,
            Status = PersistedTaskStatus.Pending,
        };
        _dbContext.PersistedTasks.AddRange(dbTaskForCh2, dbTaskForCh1);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Load them into the in-memory queue
        await _taskQueue.ReplayPendingOrFailed(TestContext.Current.CancellationToken);

        var snapshotBefore = _taskQueue.GetUpscaleSnapshot();
        Assert.Equal(2, snapshotBefore.Count);
        Assert.Equal(10, snapshotBefore[0].Order); // Ch 2
        Assert.Equal(20, snapshotBefore[1].Order); // Ch 1

        // Act
        // 1. Enqueue Chapter 1.5. Since it's < Chapter 2, it should be sorted before Chapter 2 (first opportunity).
        // It gets Order = 10, Chapter 2 gets shifted to 11, Chapter 1 gets shifted to 21.
        var taskForCh1_5 = new UpscaleTask(chapter1_5, upscalerProfile);
        await _taskQueue.EnqueueAsync(taskForCh1_5);

        // 2. Enqueue Chapter 3. Since it's > Chapter 2 and > Chapter 1, it matches nothing, so it goes to the end.
        // Order = maxOrder + 1 = 22.
        var taskForCh3 = new UpscaleTask(chapter3, upscalerProfile);
        await _taskQueue.EnqueueAsync(taskForCh3);

        // Assert
        var snapshotAfter = _taskQueue.GetUpscaleSnapshot();
        Assert.Equal(4, snapshotAfter.Count);

        // Expected sorted order: Ch 1.5 (Order 10), Ch 2 (Order 11), Ch 1 (Order 21), Ch 3 (Order 22)
        Assert.Equal(chapter1_5.Id, ((UpscaleTask)snapshotAfter[0].Data).ChapterId);
        Assert.Equal(10, snapshotAfter[0].Order);

        Assert.Equal(chapter2.Id, ((UpscaleTask)snapshotAfter[1].Data).ChapterId);
        Assert.Equal(11, snapshotAfter[1].Order);

        Assert.Equal(chapter1.Id, ((UpscaleTask)snapshotAfter[2].Data).ChapterId);
        Assert.Equal(21, snapshotAfter[2].Order);

        Assert.Equal(chapter3.Id, ((UpscaleTask)snapshotAfter[3].Data).ChapterId);
        Assert.Equal(22, snapshotAfter[3].Order);
    }
}
