namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Serializes asynchronous work so only one operation runs at a time. Re-entrant: an operation that
/// already holds the gate may call further gated operations without deadlocking, tracked per async
/// flow with <see cref="AsyncLocal{T}"/>.
/// </summary>
/// <remarks>
/// A Blazor Server circuit shares one scoped <c>ApplicationDbContext</c> between every component and
/// scoped service. If two event handlers (or a fire-and-forget handler and a background task) touch
/// it concurrently, EF throws "A second operation was started on this context instance before a
/// previous operation completed". Gating a component's entry points keeps its DB work serialized,
/// while re-entrancy lets nested calls (e.g. a commit that reloads the table) share the same slot.
/// </remarks>
public sealed class ReentrantAsyncGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly AsyncLocal<int> _depth = new();

    /// <summary>Runs <paramref name="operation"/> once the gate is free.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (_depth.Value > 0)
        {
            // Already inside a gated operation in this async flow; do not re-acquire the semaphore.
            return await operation();
        }

        await _semaphore.WaitAsync();
        _depth.Value++;
        try
        {
            return await operation();
        }
        finally
        {
            _depth.Value--;
            _semaphore.Release();
        }
    }

    /// <summary>Runs <paramref name="operation"/> once the gate is free.</summary>
    public Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(async () =>
        {
            await operation();
            return true;
        });
    }

    public void Dispose() => _semaphore.Dispose();
}
