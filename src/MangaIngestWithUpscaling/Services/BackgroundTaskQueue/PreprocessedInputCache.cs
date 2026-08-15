using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
/// Hands preprocessed inputs from the background prefetch to the matching upscale task.
/// A prefetch is registered as an in-flight promise, so the consuming task can await it
/// instead of racing it with a fallback that would duplicate the work and leak the result.
/// </summary>
public interface IPreprocessedInputCache
{
    /// <summary>
    /// Registers (or returns the existing) prefetch promise for a chapter. The prefetcher
    /// completes it with the preprocessed input, or <c>null</c> when preprocessing failed.
    /// </summary>
    TaskCompletionSource<IPreprocessedInput?> StartPrefetch(int chapterId);

    /// <summary>
    /// Consumes the prefetch for a chapter: awaits the result if a prefetch was started, or
    /// returns <c>null</c> immediately when none is in flight (caller falls back to inline).
    /// </summary>
    Task<IPreprocessedInput?> TakeAsync(int chapterId, CancellationToken cancellationToken);
}

public sealed class PreprocessedInputCache : IPreprocessedInputCache
{
    private static readonly TimeSpan StaleTimeout = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<
        int,
        TaskCompletionSource<IPreprocessedInput?>
    > _prefetches = new();

    public TaskCompletionSource<IPreprocessedInput?> StartPrefetch(int chapterId)
    {
        TaskCompletionSource<IPreprocessedInput?> tcs = _prefetches.GetOrAdd(
            chapterId,
            static _ => new TaskCompletionSource<IPreprocessedInput?>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        _ = CleanupStaleAsync(chapterId, tcs);
        return tcs;
    }

    public async Task<IPreprocessedInput?> TakeAsync(
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        if (!_prefetches.TryRemove(chapterId, out TaskCompletionSource<IPreprocessedInput?>? tcs))
        {
            return null;
        }

        try
        {
            return await tcs.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The consumer is gone. If the prefetch is still running, the entry is already
            // removed from the dictionary, so reclaim the eventual result asynchronously.
            _ = DisposeWhenCompletedAsync(tcs);
            throw;
        }
    }

    /// <summary>
    /// Reclaims a prefetch that was never consumed (e.g. its task was cancelled or claimed
    /// elsewhere after the prefetch completed), so the temp file doesn't leak.
    /// </summary>
    private async Task CleanupStaleAsync(
        int chapterId,
        TaskCompletionSource<IPreprocessedInput?> tcs
    )
    {
        try
        {
            await Task.Delay(StaleTimeout);
            if (_prefetches.TryRemove(KeyValuePair.Create(chapterId, tcs)))
            {
                await DisposeWhenCompletedAsync(tcs);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Waits for the prefetch to finish and disposes its result. Used whenever the promise has
    /// been removed from the dictionary without being handed to a consumer.
    /// </summary>
    private static async Task DisposeWhenCompletedAsync(
        TaskCompletionSource<IPreprocessedInput?> tcs
    )
    {
        try
        {
            IPreprocessedInput? preprocessed = await tcs.Task;
            preprocessed?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
