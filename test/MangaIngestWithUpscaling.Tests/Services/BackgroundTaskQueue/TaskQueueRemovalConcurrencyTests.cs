using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class TaskQueueRemovalConcurrencyTests : IDisposable
{
    private readonly DeleteBeforeSaveInterceptor _interceptor = new();
    private readonly string _dbFile;
    private readonly ServiceProvider _provider;
    private readonly TaskQueue _taskQueue;

    public TaskQueueRemovalConcurrencyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _dbFile = Path.Combine(Path.GetTempPath(), $"taskqueue-concurrency-{Guid.NewGuid():N}.db");
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={_dbFile}").AddInterceptors(_interceptor)
        );

        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);

        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        _taskQueue = new TaskQueue(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<TaskQueue>>()
        );
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbFile);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp database.
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTaskAsync_WhenRowDeletedBeforeSave_DoesNotThrowAndRowStaysGone()
    {
        int id = await SeedTaskAsync();

        // Simulate a concurrent remover (another call or QueueCleanup) deleting the row after the
        // queue loaded it but before its SaveChanges runs.
        _interceptor.Enabled = true;
        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() =>
                _taskQueue.RemoveTaskAsync(new PersistedTask { Id = id })
            );
        }
        finally
        {
            _interceptor.Enabled = false;
        }

        Assert.Null(exception);
        Assert.Null(await FindTaskAsync(id));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WhenRowsDeletedBeforeSave_DoesNotThrowAndRowsStayGone()
    {
        int first = await SeedTaskAsync();
        int second = await SeedTaskAsync();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        List<PersistedTask> tasks = await db
            .PersistedTasks.AsNoTracking()
            .Where(t => t.Id == first || t.Id == second)
            .ToListAsync(TestContext.Current.CancellationToken);

        _interceptor.Enabled = true;
        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() => _taskQueue.RemoveTasksAsync(tasks));
        }
        finally
        {
            _interceptor.Enabled = false;
        }

        Assert.Null(exception);
        Assert.Null(await FindTaskAsync(first));
        Assert.Null(await FindTaskAsync(second));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WhenOnlySubsetDeletedBeforeSave_RemovesRemainingRowsAndClearsMemory()
    {
        // A concurrent remover that only takes one row of a batch must not let a rolled-back
        // SaveChanges leave the other rows in the database while memory drops them all.
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "a" });
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "b" });
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "c" });
        List<PersistedTask> tasks = _taskQueue.GetStandardSnapshot().ToList();
        Assert.Equal(3, tasks.Count);

        int racedId = tasks[1].Id;
        var removedIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        _taskQueue.TaskRemoved += task =>
        {
            removedIds.Add(task.Id);
            return Task.CompletedTask;
        };

        _interceptor.Enabled = true;
        _interceptor.IdsToDelete.Add(racedId);
        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() => _taskQueue.RemoveTasksAsync(tasks));
        }
        finally
        {
            _interceptor.Enabled = false;
            _interceptor.IdsToDelete.Clear();
        }

        // The raced row was already gone; the other two must still be deleted.
        Assert.Null(exception);
        foreach (PersistedTask task in tasks)
        {
            Assert.Null(await FindTaskAsync(task.Id));
        }

        Assert.Empty(_taskQueue.GetStandardSnapshot());
        Assert.Equal(3, removedIds.Distinct().Count());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTaskAsync_ConcurrentCalls_DoNotThrowAndRowIsGone()
    {
        int id = await SeedTaskAsync();

        var errors = new ConcurrentBag<Exception>();
        using var barrier = new Barrier(2);

        async Task RemoveAsync()
        {
            barrier.SignalAndWait();
            try
            {
                await _taskQueue.RemoveTaskAsync(new PersistedTask { Id = id });
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        await Task.WhenAll(
                Task.Run(RemoveAsync, TestContext.Current.CancellationToken),
                Task.Run(RemoveAsync, TestContext.Current.CancellationToken)
            )
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(errors);
        Assert.Null(await FindTaskAsync(id));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTasksAsync_WhenDeletesKeepConflicting_LeavesSurvivingTasksInMemory()
    {
        // Regression guard: when the delete retry budget was exhausted, the batch removal still
        // cleared memory and announced removals, so the surviving rows could reappear on restart.
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "a" });
        await _taskQueue.EnqueueAsync(new LoggingTask { Message = "b" });
        List<PersistedTask> tasks = _taskQueue.GetStandardSnapshot().ToList();
        Assert.Equal(2, tasks.Count);

        var removedIds = new ConcurrentBag<int>();
        _taskQueue.TaskRemoved += task =>
        {
            removedIds.Add(task.Id);
            return Task.CompletedTask;
        };

        _interceptor.Enabled = true;
        _interceptor.ThrowConcurrency = true;
        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() => _taskQueue.RemoveTasksAsync(tasks));
        }
        finally
        {
            _interceptor.Enabled = false;
            _interceptor.ThrowConcurrency = false;
        }

        Assert.Null(exception);
        // Every delete failed, so no row is confirmed gone: the tasks must stay in memory and must
        // not be announced as removed.
        Assert.Equal(2, _taskQueue.GetStandardSnapshot().Count);
        Assert.Empty(removedIds);
        foreach (PersistedTask task in tasks)
        {
            Assert.NotNull(await FindTaskAsync(task.Id));
        }
    }

    private async Task<int> SeedTaskAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        int order = await db.PersistedTasks.CountAsync(TestContext.Current.CancellationToken) + 1;
        var task = new PersistedTask
        {
            Data = new LoggingTask { Message = "seed" },
            Status = PersistedTaskStatus.Pending,
            Order = order,
        };
        db.PersistedTasks.Add(task);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return task.Id;
    }

    private async Task<PersistedTask?> FindTaskAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Deletes the rows a <see cref="ApplicationDbContext" /> is about to delete, just before its
    ///     SaveChanges runs. This deterministically reproduces the "row vanished after the existence
    ///     check" race that makes EF throw <see cref="DbUpdateConcurrencyException" /> on SQLite.
    /// </summary>
    private sealed class DeleteBeforeSaveInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        /// <summary>
        ///     When non-empty, only these ids are deleted before the save; otherwise every row the
        ///     save is about to delete is removed. Targeting a subset reproduces a race where only
        ///     part of a batch is taken concurrently.
        /// </summary>
        public HashSet<int> IdsToDelete { get; } = new();

        /// <summary>
        ///     When set, every save throws <see cref="DbUpdateConcurrencyException" /> without
        ///     deleting anything, simulating a delete that can never make progress.
        /// </summary>
        public bool ThrowConcurrency { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (Enabled && ThrowConcurrency)
            {
                throw new DbUpdateConcurrencyException("Simulated concurrency conflict.");
            }

            if (Enabled && eventData.Context is { } context)
            {
                List<int> ids = context
                    .ChangeTracker.Entries<PersistedTask>()
                    .Where(e => e.State == EntityState.Deleted)
                    .Select(e => e.Entity.Id)
                    .Where(id => IdsToDelete.Count == 0 || IdsToDelete.Contains(id))
                    .ToList();

                foreach (int id in ids)
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "DELETE FROM PersistedTasks WHERE Id = {0}",
                        new object[] { id },
                        cancellationToken
                    );
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
