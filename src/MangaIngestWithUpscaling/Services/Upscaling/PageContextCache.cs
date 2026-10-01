using System.Collections.Concurrent;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Process-local cache of resolved page contexts. Resolving a context scans the source archive and,
/// for repairs, diffs it against the upscaled chapter; because every page RPC resolves the context,
/// an N-page chapter would otherwise rescan its archive O(N) times.
///
/// Entries hold only derived, plain data (never EF entities), so each call still loads and tracks
/// its own entities from its own scope. An entry is only reused while the caller's freshly computed
/// identity matches the cached one, so a changed source, profile or repair state is picked up.
/// </summary>
public sealed class PageContextCache
{
    /// <summary>Derived, entity-free data for one resolved task context.</summary>
    public sealed record Entry(
        string Identity,
        string SourcePath,
        IReadOnlyList<SpoolPageDescriptor> Pages,
        int DetectorVersion,
        int ChapterId,
        int ProfileId,
        string? UpscaledFullPath,
        IReadOnlyList<string> MissingPages
    );

    private sealed class CachedEntry(Entry value)
    {
        public Entry Value { get; } = value;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<int, CachedEntry> _entries = new();

    public bool TryGet(int taskId, out Entry entry)
    {
        if (_entries.TryGetValue(taskId, out CachedEntry? cached))
        {
            cached.LastUsedUtc = DateTime.UtcNow;
            entry = cached.Value;
            return true;
        }

        entry = null!;
        return false;
    }

    public void Set(int taskId, Entry entry) => _entries[taskId] = new CachedEntry(entry);

    public void Remove(int taskId) => _entries.TryRemove(taskId, out _);

    /// <summary>Drops entries unused for longer than <paramref name="retention"/>.</summary>
    public void Sweep(TimeSpan retention)
    {
        DateTime cutoff = DateTime.UtcNow - retention;
        foreach ((int taskId, CachedEntry cached) in _entries)
        {
            if (cached.LastUsedUtc < cutoff)
            {
                _entries.TryRemove(taskId, out _);
            }
        }
    }
}
