namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Turns upscale progress into prefetch decisions. Shared between the local pipeline
/// (preprocess prefetch) and the remote worker (download prefetch), so improvements to
/// the decision logic benefit both.
/// </summary>
public sealed class PrefetchCoordinator
{
    private readonly PrefetchPredictor _predictor = new();
    private readonly Lock _lock = new();
    private int _signaled;
    private int? _lastCurrent;
    private DateTime _lastProgressTime = DateTime.UtcNow;

    /// <summary>Records how long a single prefetch (download or preprocess) took.</summary>
    public void RecordPrefetch(TimeSpan elapsed)
    {
        lock (_lock)
        {
            _predictor.RecordPrefetch(elapsed);
        }
    }

    /// <summary>Resets per-job state before processing a new job.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _signaled = 0;
            _lastCurrent = null;
            _lastProgressTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Feeds a progress update and returns <c>true</c> once per job when a prefetch should be
    /// triggered.
    /// </summary>
    public bool OnProgress(int? total, int? current)
    {
        if (total is not { } totalPages || current is not { } currentPages || totalPages <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            if (_lastCurrent is { } last && currentPages > last)
            {
                double deltaTime = (DateTime.UtcNow - _lastProgressTime).TotalSeconds;
                int deltaPages = currentPages - last;
                if (deltaPages > 0 && deltaTime > 0)
                {
                    _predictor.RecordPerPage(deltaTime / deltaPages);
                }
            }

            _lastCurrent = currentPages;
            _lastProgressTime = DateTime.UtcNow;

            int remaining = Math.Max(0, totalPages - currentPages);
            if (_predictor.ShouldPrefetch(remaining, totalPages))
            {
                if (_signaled == 0)
                {
                    _signaled = 1;
                    return true;
                }
            }

            return false;
        }
    }
}
