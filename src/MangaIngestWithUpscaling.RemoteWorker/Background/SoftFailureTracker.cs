namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Owns the per-task consecutive soft (transient/restart) failure counter, so a deterministically
/// failing task is escalated to a reported failure instead of cycling forever — even if other tasks
/// fail in between (a single "last task" counter could be reset by an interleaved task). Only the
/// streaming loop touches an instance, so it is not synchronized.
/// </summary>
public sealed class SoftFailureTracker
{
    /// <summary>
    /// How many consecutive soft (transient/restart) failures a task may accumulate before the worker
    /// reports it as a hard failure. The server's dead-task reaper requeues without consuming the
    /// retry budget, so without a cap a deterministically-failing task would cycle forever.
    /// </summary>
    public const int MaxConsecutiveSoftFailures = 5;

    /// <summary>
    /// A restart is the "no spool on this replica / identity changed" signal, which a misconfigured
    /// deployment can produce indefinitely; use a much larger cap so it eventually surfaces without
    /// turning a transient infrastructure issue into data loss.
    /// </summary>
    public const int MaxConsecutiveRestarts = 100;

    private readonly Dictionary<int, int> _counts = new();

    /// <summary>Records another consecutive failure for <paramref name="taskId" />.</summary>
    /// <returns>The new consecutive-failure count.</returns>
    public int Increment(int taskId)
    {
        _counts.TryGetValue(taskId, out int count);
        count++;
        _counts[taskId] = count;
        return count;
    }

    /// <summary>
    /// Forgets <paramref name="taskId" />, e.g. after it completes or is terminalized, so the map does
    /// not grow one entry per permanently-failed task.
    /// </summary>
    public void Reset(int taskId) => _counts.Remove(taskId);

    /// <summary>The consecutive-failure count at which <paramref name="kind" /> is terminalized.</summary>
    public static int CapFor(RemoteTaskProcessor.StreamingFailureKind kind) =>
        kind == RemoteTaskProcessor.StreamingFailureKind.Restart
            ? MaxConsecutiveRestarts
            : MaxConsecutiveSoftFailures;
}
