using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
/// Holds preprocessed inputs produced by the prefetch pipeline until the matching
/// upscale task consumes them.
/// </summary>
public interface IPreprocessedInputCache
{
    bool TryTake(int chapterId, out IPreprocessedInput preprocessed);
    void Store(int chapterId, IPreprocessedInput preprocessed);
}

public sealed class PreprocessedInputCache : IPreprocessedInputCache
{
    private readonly ConcurrentDictionary<int, IPreprocessedInput> _entries = new();

    public bool TryTake(int chapterId, out IPreprocessedInput preprocessed) =>
        _entries.TryRemove(chapterId, out preprocessed!);

    public void Store(int chapterId, IPreprocessedInput preprocessed)
    {
        // Replace any stale entry for the same chapter so a second prefetch doesn't leak.
        if (_entries.TryRemove(chapterId, out IPreprocessedInput? old))
        {
            old.Dispose();
        }

        _entries[chapterId] = preprocessed;
    }
}
