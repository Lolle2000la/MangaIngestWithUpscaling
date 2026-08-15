namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Implements Welford's method for online computation of statistics.
/// Used to estimate prefetch and per-page processing times for predictive prefetching.
/// </summary>
public sealed class OnlineStats
{
    public int Count { get; private set; }
    public double Mean { get; private set; }
    public double M2 { get; private set; }

    public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0.0;
    public double P95Upper => Mean + (1.96 * StdDev);

    public void Add(double x)
    {
        Count++;
        double delta = x - Mean;
        Mean += delta / Count;
        double delta2 = x - Mean;
        M2 += delta * delta2;
    }
}

/// <summary>
/// Decides when to trigger a prefetch based on rolling statistics of the prefetch
/// duration and the per-page processing time. Shared between the local pipeline
/// (preprocess prefetch) and the remote worker (download prefetch).
/// </summary>
public sealed class PrefetchPredictor
{
    private readonly OnlineStats _prefetchSeconds = new();
    private readonly OnlineStats _perPageSeconds = new();

    /// <summary>Records how long a single prefetch (download or preprocess) took.</summary>
    public void RecordPrefetch(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds > 0 && double.IsFinite(elapsed.TotalSeconds))
        {
            _prefetchSeconds.Add(elapsed.TotalSeconds);
        }
    }

    public void RecordPerPage(double secondsPerPage)
    {
        if (secondsPerPage > 0 && double.IsFinite(secondsPerPage))
        {
            _perPageSeconds.Add(secondsPerPage);
        }
    }

    /// <summary>
    /// Whether to trigger a prefetch now, given how many pages remain in the in-flight job.
    /// </summary>
    public bool ShouldPrefetch(int remainingPages, int totalPages)
    {
        if (totalPages <= 0)
        {
            return false;
        }

        bool quarterLeft = remainingPages <= (int)Math.Ceiling(totalPages * 0.25);
        bool fiveLeft = remainingPages <= 5;

        bool etaTrigger = false;
        if (_prefetchSeconds.Count > 0 && _perPageSeconds.Count > 0)
        {
            double prefetch95 = _prefetchSeconds.P95Upper;
            double perPage95 = _perPageSeconds.P95Upper;
            double remainingEta95 = remainingPages * perPage95;
            etaTrigger = remainingEta95 <= prefetch95;
        }

        return quarterLeft || fiveLeft || etaTrigger;
    }
}
