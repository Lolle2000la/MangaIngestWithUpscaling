using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class PendingTaskChangesTests
{
    private static PersistedTask CreateTask(int id) =>
        new()
        {
            Id = id,
            Data = new LoggingTask { Message = $"task-{id}" },
        };

    [Fact]
    [Trait("Category", "Unit")]
    public void QueueUpdate_ThenQueueRemoval_RemovalWins()
    {
        var buffer = new PendingTaskChanges();
        var task = CreateTask(1);

        buffer.QueueUpdate(task);
        buffer.QueueRemoval(task);

        buffer.Drain(out var updates, out var removals);

        Assert.Empty(updates);
        Assert.Single(removals);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QueueRemoval_ThenQueueUpdate_UpdateIsIgnored()
    {
        var buffer = new PendingTaskChanges();
        var task = CreateTask(2);

        buffer.QueueRemoval(task);
        buffer.QueueUpdate(task);

        buffer.Drain(out var updates, out var removals);

        Assert.Empty(updates);
        Assert.Single(removals);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QueueUpdate_AfterRemovalWasDrained_IsIgnored()
    {
        var buffer = new PendingTaskChanges();
        var task = CreateTask(3);

        buffer.QueueRemoval(task);
        buffer.Drain(out _, out var removals);
        Assert.Single(removals);

        // The removal was applied in an earlier batch, so there is no colliding removal to drop
        // this update. Only the persistent tombstone filter can stop it from resurrecting the task.
        buffer.QueueUpdate(task);
        buffer.Drain(out var updates, out _);

        Assert.Empty(updates);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QueueUpdate_ForManyTasks_KeepsAllUpdates()
    {
        var buffer = new PendingTaskChanges();

        for (int i = 1; i <= 100; i++)
        {
            buffer.QueueUpdate(CreateTask(i));
        }

        buffer.Drain(out var updates, out var removals);

        Assert.Equal(100, updates.Count);
        Assert.Empty(removals);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Drain_NeverReturnsSameIdInUpdatesAndRemovals_UnderConcurrency()
    {
        // Regression guard: an update and a removal for the same task can be queued from different
        // threads. A removed task must never be resurrected by an update in the same batch.
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var buffer = new PendingTaskChanges();
            var task = CreateTask(iteration + 1);

            Task update = Task.Run(
                () => buffer.QueueUpdate(task),
                TestContext.Current.CancellationToken
            );
            Task removal = Task.Run(
                () => buffer.QueueRemoval(task),
                TestContext.Current.CancellationToken
            );

            await Task.WhenAll(update, removal);

            buffer.Drain(out var updates, out var removals);

            HashSet<int> updateIds = updates.Select(u => u.Id).ToHashSet();
            HashSet<int> removalIds = removals.Select(r => r.Id).ToHashSet();
            Assert.Empty(updateIds.Intersect(removalIds));
        }
    }
}
