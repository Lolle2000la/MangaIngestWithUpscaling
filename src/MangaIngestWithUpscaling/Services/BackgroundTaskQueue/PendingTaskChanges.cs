using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Buffers task updates and removals so the registry can apply them in a single batch.
/// </summary>
/// <remarks>
///     Updates and removals arrive from different threads (processor status events versus
///     queue/UI removal calls). Within a single batch, a removal always wins over an update for
///     the same task. Across batches, <see cref="TaskRegistry" /> remembers removed task ids so a
///     late status update cannot resurrect a task whose row has already been deleted.
/// </remarks>
internal sealed class PendingTaskChanges
{
    private readonly ConcurrentDictionary<int, PersistedTask> _updates = new();
    private readonly ConcurrentDictionary<int, PersistedTask> _removals = new();

    public bool IsEmpty => _updates.IsEmpty && _removals.IsEmpty;

    /// <summary>
    ///     Queues an update. A removal queued before or after it in the same batch takes precedence
    ///     (see <see cref="Drain" />).
    /// </summary>
    public void QueueUpdate(PersistedTask task) => _updates[task.Id] = task;

    /// <summary>
    ///     Queues a removal and drops any pending update for the same task.
    /// </summary>
    public void QueueRemoval(PersistedTask task)
    {
        _updates.TryRemove(task.Id, out _);
        _removals[task.Id] = task;
    }

    /// <summary>
    ///     Drains the buffered changes. No task id is ever returned in both <paramref name="updates" />
    ///     and <paramref name="removals" />, regardless of how the producers interleaved.
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

        // An update that slipped in while the removals were being drained must not resurrect a
        // task that is being removed in this same batch.
        if (removals.Count > 0 && updates.Count > 0)
        {
            HashSet<int> removalIds = removals.Select(r => r.Id).ToHashSet();
            updates.RemoveAll(u => removalIds.Contains(u.Id));
        }
    }
}
