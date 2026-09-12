using System.Collections.Concurrent;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Remembers recently removed task ids so a late status update cannot resurrect a task after
///     its removal has already been applied.
/// </summary>
/// <remarks>
///     Task ids come from a database identity and are never reused, so a removed id stays removed.
///     The set is bounded to avoid unbounded growth in long-running processes; the eviction window
///     is many orders of magnitude larger than the time in which a late update can arrive.
/// </remarks>
internal sealed class RemovedTaskIds
{
    // A removed id is only relevant until any in-flight status update for it has been drained. The
    // cap is far larger than that window; keeping it bounded avoids unbounded growth in long-running
    // processes. Raised from 50_000 to 200_000: still small in memory, but widens the safety margin.
    private const int MaxTracked = 200_000;

    private readonly ConcurrentDictionary<int, byte> _ids = new();
    private readonly ConcurrentQueue<int> _insertionOrder = new();

    public bool Contains(int id) => _ids.ContainsKey(id);

    public void Add(int id)
    {
        if (!_ids.TryAdd(id, 0))
        {
            return;
        }

        _insertionOrder.Enqueue(id);

        // Evict oldest ids first. They are far outside any realistic late-update window.
        while (_insertionOrder.Count > MaxTracked && _insertionOrder.TryDequeue(out int evicted))
        {
            _ids.TryRemove(evicted, out _);
        }
    }
}
