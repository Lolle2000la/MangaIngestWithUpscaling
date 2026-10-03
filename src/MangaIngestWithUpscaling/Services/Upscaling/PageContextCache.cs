using System.Collections.Concurrent;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Process-local cache of resolved page contexts. Resolving a context scans the source archive and,
/// for repairs, diffs it against the upscaled chapter; because every page RPC resolves the context,
/// an N-page chapter would otherwise rescan its archive O(N) times. The cache removes those scans
/// (not the per-RPC entity loads or the identity computation).
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
        IReadOnlyList<SpoolPageDescriptor> Pages,
        IReadOnlyList<string> MissingPages
    )
    {
        /// <summary>
        /// Largest page (in pixels) in the source archive, computed once and reused by later
        /// manifests. The value is derived from the immutable source archive, so re-decoding the
        /// whole archive on every manifest (a resume re-manifests each attempt) is wasted work.
        /// Written after an await from a singleton-shared entry, so use Volatile for the long.
        /// </summary>
        private long _maxPagePixels;

        public long MaxPagePixels
        {
            get => Volatile.Read(ref _maxPagePixels);
            set => Volatile.Write(ref _maxPagePixels, value);
        }
    }

    private sealed class CachedEntry(Entry value)
    {
        public Entry Value { get; } = value;
        private long _lastUsedTicks = DateTime.UtcNow.Ticks;

        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

        public DateTime LastUsedUtc => new(Volatile.Read(ref _lastUsedTicks), DateTimeKind.Utc);
    }

    private readonly ConcurrentDictionary<int, CachedEntry> _entries = new();

    public bool TryGet(int taskId, out Entry entry)
    {
        if (_entries.TryGetValue(taskId, out CachedEntry? cached))
        {
            cached.Touch();
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
