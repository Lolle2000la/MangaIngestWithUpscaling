namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Serializes asynchronous work so only one operation runs at a time.
/// </summary>
/// <remarks>
/// A Blazor Server circuit shares one scoped <c>ApplicationDbContext</c> between every component and
/// scoped service. If two event handlers (or a fire-and-forget handler and a background task) touch
/// it concurrently, EF throws "A second operation was started on this context instance before a
/// previous operation completed". Gating a component's DB entry points keeps its own DB work
/// serialized.
/// <para>
/// The gate is intentionally <b>not</b> re-entrant. Re-entrancy would require recognising "the same
/// async flow", but <see cref="AsyncLocal{T}"/> cannot tell a continuation of the holder apart from a
/// task the holder spawned: a fire-and-forget task or <c>ContinueWith</c> started inside a gated
/// operation inherits the "already holds the gate" marker and silently bypasses the semaphore,
/// reintroducing the very overlap the gate exists to prevent. Callers must therefore never await a
/// gated operation from inside a gated operation; nested DB work calls the <c>*Core</c> method
/// directly instead.
/// </para>
/// <para>
/// A gate only serializes the component instance that owns it. The scoped context is still shared
/// with other components, dialogs and services; those are expected to be idle (or awaited) while a
/// gated operation runs.
/// </para>
/// </remarks>
public sealed class AsyncGate
{
    // Deliberately never disposed: a component can be disposed while an operation it started is
    // still queued or running, and disposing the semaphore would leave a queued waiter hanging
    // forever and turn the holder's Release() into an ObjectDisposedException. SemaphoreSlim only
    // needs disposal when AvailableWaitHandle is used, which this gate never does.
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Runs <paramref name="operation"/> once the gate is free.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _semaphore.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>Runs <paramref name="operation"/> once the gate is free.</summary>
    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _semaphore.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
