using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Buffers task updates and removals so the registry can apply them in a single batch.
/// </summary>
/// <remarks>
///     Updates and removals arrive from different threads (processor status events versus
///     queue/UI removal calls). Removals are authoritative: <see cref="Drain" /> drops every
///     buffered update whose task id has ever been removed, not just ones colliding with a removal
///     in the same batch. This is what makes a late status update unable to resurrect a removed
///     task even if the update passed an earlier check before the removal was recorded.
/// </remarks>
internal sealed class PendingTaskChanges
{
    private readonly ConcurrentDictionary<int, PersistedTask> _updates = new();
    private readonly ConcurrentDictionary<int, PersistedTask> _removals = new();
    private readonly RemovedTaskIds _removedIds = new();

    public bool IsEmpty => _updates.IsEmpty && _removals.IsEmpty;

    /// <summary>
    ///     Queues an update. The removal filter in <see cref="Drain" /> is authoritative, so this
    ///     does not need to consult the removed-id set.
    /// </summary>
    public void QueueUpdate(PersistedTask task) => _updates[task.Id] = task;

    /// <summary>
    ///     Queues a removal, records the id as permanently removed, and drops any pending update
    ///     for the same task.
    /// </summary>
    public void QueueRemoval(PersistedTask task)
    {
        _removedIds.Add(task.Id);
        _updates.TryRemove(task.Id, out _);
        _removals[task.Id] = task;
    }

    /// <summary>
    ///     Drains the buffered changes. An update for a removed task is never returned, regardless
    ///     of how the producers interleaved or whether the removal was applied in an earlier batch.
    /// </summary>
    public void Drain(out List<PersistedTask> updates, out List<PersistedTask> removals)
    {
        updates = new List<PersistedTask>();
        removals = new List<PersistedTask>();

        foreach (var key in _updates.Keys.ToList())
        {
            if (_updates.TryRemove(key, out var update))
            {
                updates.Add(update);
            }
        }

        foreach (var key in _removals.Keys.ToList())
        {
            if (_removals.TryRemove(key, out var removal))
            {
                removals.Add(removal);
            }
        }

        // Removals win across batches: a status update that was already in flight when a previous
        // batch removed the task must not bring it back.
        if (updates.Count > 0)
        {
            updates.RemoveAll(u => _removedIds.Contains(u.Id));
        }
    }
}
