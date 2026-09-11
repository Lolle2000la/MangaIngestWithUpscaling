using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Buffers task updates and removals so the registry can apply them in a single batch.
/// </summary>
/// <remarks>
///     Updates and removals arrive from different threads (processor status events versus
///     queue/UI removal calls), so without care a task could be queued for both and be
///     re-inserted after it was removed. Removals therefore always take precedence over updates,
///     regardless of the order in which the producers queued them.
/// </remarks>
internal sealed class PendingTaskChanges
{
    private readonly ConcurrentDictionary<int, PersistedTask> _updates = new();
    private readonly ConcurrentDictionary<int, PersistedTask> _removals = new();

    public bool IsEmpty => _updates.IsEmpty && _removals.IsEmpty;

    /// <summary>
    ///     Queues an update, unless the task is (or becomes) queued for removal.
    /// </summary>
    public void QueueUpdate(PersistedTask task)
    {
        if (_removals.ContainsKey(task.Id))
        {
            return;
        }

        _updates[task.Id] = task;

        // Close the race with a concurrent QueueRemoval: if the removal appeared between the
        // check above and this assignment, discard the update so removal wins.
        if (_removals.ContainsKey(task.Id))
        {
            _updates.TryRemove(task.Id, out _);
        }
    }

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
    ///     and <paramref name="removals" />.
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

        // Filter explicitly as a final guarantee: an update that slipped in during the drain must
        // not resurrect a task that is being removed in this same batch.
        if (removals.Count > 0 && updates.Count > 0)
        {
            HashSet<int> removalIds = removals.Select(r => r.Id).ToHashSet();
            updates.RemoveAll(u => removalIds.Contains(u.Id));
        }
    }
}
