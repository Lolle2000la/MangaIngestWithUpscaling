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
    private readonly ConcurrentDictionary<
        int,
        TaskCompletionSource<IPreprocessedInput?>
    > _prefetches = new();

    public TaskCompletionSource<IPreprocessedInput?> StartPrefetch(int chapterId) =>
        _prefetches.GetOrAdd(
            chapterId,
            static _ => new TaskCompletionSource<IPreprocessedInput?>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );

    public async Task<IPreprocessedInput?> TakeAsync(
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        if (!_prefetches.TryRemove(chapterId, out TaskCompletionSource<IPreprocessedInput?>? tcs))
        {
            return null;
        }

        return await tcs.Task.WaitAsync(cancellationToken);
    }
}
