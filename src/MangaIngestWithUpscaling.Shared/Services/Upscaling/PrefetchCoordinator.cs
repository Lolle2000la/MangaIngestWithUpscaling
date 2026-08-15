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
    private int? _lastRemainingPages;
    private DateTime _lastProgressTime = DateTime.UtcNow;

    /// <summary>Records how long a single prefetch (download or preprocess) took.</summary>
    public void RecordPrefetch(TimeSpan elapsed)
    {
        lock (_lock)
        {
            _predictor.RecordPrefetch(elapsed);
        }
    }

    /// <summary>Records a completed download by size and duration for bandwidth-based prediction.</summary>
    public void RecordDownload(long bytes, TimeSpan elapsed)
    {
        lock (_lock)
        {
            _predictor.RecordDownload(bytes, elapsed);
        }
    }

    /// <summary>Resets per-job state before processing a new job.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _signaled = 0;
            _lastCurrent = null;
            _lastRemainingPages = null;
            _lastProgressTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Feeds a progress update and returns <c>true</c> once per job when a prefetch should be
    /// triggered. A <paramref name="phase"/> of "finalizing" triggers immediately: the GPU has
    /// freed up (only archive I/O remains) even when page counts are missing or stale.
    /// </summary>
    public bool OnProgress(int? total, int? current, string? phase = null)
    {
        bool finalizing = string.Equals(phase, "finalizing", StringComparison.OrdinalIgnoreCase);

        lock (_lock)
        {
            int totalPages;
            int remainingPages;

            if (finalizing)
            {
                // Treat the job as having no remaining pages so both prefetch and remote claim
                // timing act on the now-idle GPU.
                _lastRemainingPages = 0;
                totalPages = 0;
                remainingPages = 0;
            }
            else if (total is { } tp && current is { } cp && tp > 0)
            {
                if (_lastCurrent is { } last && cp > last)
                {
                    double deltaTime = (DateTime.UtcNow - _lastProgressTime).TotalSeconds;
                    int deltaPages = cp - last;
                    if (deltaPages > 0 && deltaTime > 0)
                    {
                        _predictor.RecordPerPage(deltaTime / deltaPages);
                    }
                }

                _lastCurrent = cp;
                _lastProgressTime = DateTime.UtcNow;
                remainingPages = Math.Max(0, tp - cp);
                _lastRemainingPages = remainingPages;
                totalPages = tp;
            }
            else
            {
                // No usable page counts and not finalizing: nothing to decide on.
                return false;
            }

            if (_predictor.ShouldPrefetch(remainingPages, totalPages, finalizing))
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

    /// <summary>
    /// Whether a task of the given transfer size should be claimed now, using the most recent
    /// remaining-page estimate and the bandwidth/per-page statistics.
    /// </summary>
    public bool ShouldClaim(long bytes)
    {
        lock (_lock)
        {
            return _predictor.ShouldClaim(bytes, _lastRemainingPages ?? 0);
        }
    }
}
