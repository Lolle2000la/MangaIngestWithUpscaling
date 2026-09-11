using System.Collections.Concurrent;
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
        // Regression guard: updates, removals and drains for the same tasks run concurrently. A
        // removed task must never be returned as an update in the same batch as its removal.
        const int iterations = 50;
        const int taskCount = 40;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var buffer = new PendingTaskChanges();
            using var start = new ManualResetEventSlim(false);
            var errors = new ConcurrentBag<Exception>();

            var producers = Enumerable
                .Range(0, 3)
                .Select(_ =>
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                start.Wait(TestContext.Current.CancellationToken);
                                for (int i = 1; i <= taskCount; i++)
                                {
                                    PersistedTask task = CreateTask(i);
                                    buffer.QueueUpdate(task);
                                    await Task.Yield();
                                    buffer.QueueRemoval(task);
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add(ex);
                            }
                        },
                        TestContext.Current.CancellationToken
                    )
                )
                .ToList();

            var drainer = Task.Run(
                async () =>
                {
                    try
                    {
                        start.Wait(TestContext.Current.CancellationToken);
                        for (int i = 0; i < taskCount; i++)
                        {
                            buffer.Drain(out var updates, out var removals);

                            HashSet<int> updateIds = updates.Select(u => u.Id).ToHashSet();
                            HashSet<int> removalIds = removals.Select(r => r.Id).ToHashSet();
                            if (updateIds.Overlaps(removalIds))
                            {
                                throw new InvalidOperationException(
                                    "An id was returned as both an update and a removal in the same drain batch."
                                );
                            }

                            await Task.Yield();
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                },
                TestContext.Current.CancellationToken
            );

            start.Set();

            await Task.WhenAll(producers.Append(drainer))
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Empty(errors);

            buffer.Drain(out var remainingUpdates, out var remainingRemovals);
            HashSet<int> remainingUpdateIds = remainingUpdates.Select(u => u.Id).ToHashSet();
            HashSet<int> remainingRemovalIds = remainingRemovals.Select(r => r.Id).ToHashSet();
            Assert.Empty(remainingUpdateIds.Intersect(remainingRemovalIds));
        }
    }
}
