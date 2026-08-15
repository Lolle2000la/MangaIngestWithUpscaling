namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Online statistics: Welford's method for mean/variance plus a ring buffer of recent samples
/// for real quantiles. The quantiles matter because durations and bandwidth are heavy-tailed:
/// a normal-distribution bound (mean + k·σ) under-estimates the slow tail, which is the wrong
/// direction when the goal is to prefetch early enough on a slow link.
/// </summary>
public sealed class OnlineStats
{
    private readonly double[] _samples;
    private int _writeIndex;
    private int _samplesRecorded;

    public OnlineStats(int sampleCapacity = 64)
    {
        _samples = new double[Math.Max(1, sampleCapacity)];
    }

    public int Count { get; private set; }
    public double Mean { get; private set; }
    public double M2 { get; private set; }

    public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0.0;
    public double P95Upper => Mean + (1.96 * StdDev);

    /// <summary>Median of the recent samples (50th percentile).</summary>
    public double Median => Quantile(0.5);

    public void Add(double x)
    {
        Count++;
        double delta = x - Mean;
        Mean += delta / Count;
        double delta2 = x - Mean;
        M2 += delta * delta2;

        _samples[_writeIndex] = x;
        _writeIndex = (_writeIndex + 1) % _samples.Length;
        if (_samplesRecorded < _samples.Length)
        {
            _samplesRecorded++;
        }
    }

    /// <summary>
    /// The <paramref name="p"/>-th percentile (0..1) of the recent samples, using linear
    /// interpolation between the two nearest order statistics. Returns NaN with no samples.
    /// </summary>
    public double Quantile(double p)
    {
        if (p is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p));
        }

        if (_samplesRecorded == 0)
        {
            return double.NaN;
        }

        var sorted = new double[_samplesRecorded];
        for (int i = 0; i < _samplesRecorded; i++)
        {
            int index = (_writeIndex - _samplesRecorded + i + _samples.Length) % _samples.Length;
            sorted[i] = _samples[index];
        }

        Array.Sort(sorted);

        double rank = p * (_samplesRecorded - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double fraction = rank - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
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

    // Remote downloads are modeled as bandwidth (bytes/sec) × transfer size (bytes), tracked
    // separately so the next download time can be predicted as medianBytes / slowBandwidth.
    // Using the previous task's raw duration instead would conflate file size with link speed.
    private readonly OnlineStats _downloadBytesPerSecond = new();
    private readonly OnlineStats _downloadBytes = new();

    /// <summary>Records how long a single prefetch (download or preprocess) took.</summary>
    public void RecordPrefetch(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds > 0 && double.IsFinite(elapsed.TotalSeconds))
        {
            _prefetchSeconds.Add(elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Records a completed download by its size and duration, feeding the bandwidth and size
    /// statistics used to predict future downloads.
    /// </summary>
    public void RecordDownload(long bytes, TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        if (bytes > 0 && seconds > 0 && double.IsFinite(seconds))
        {
            _downloadBytes.Add(bytes);
            _downloadBytesPerSecond.Add(bytes / seconds);
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
        double prefetchSeconds = EstimatePrefetchSeconds();
        if (double.IsFinite(prefetchSeconds) && prefetchSeconds > 0 && _perPageSeconds.Count > 0)
        {
            double perPage95 = _perPageSeconds.P95Upper;
            double remainingEta95 = remainingPages * perPage95;
            etaTrigger = remainingEta95 <= prefetchSeconds;
        }

        return quarterLeft || fiveLeft || etaTrigger;
    }

    /// <summary>
    /// Estimated time to fetch the next item. Prefers the bandwidth/size model when download
    /// samples exist (median size / slow bandwidth, erring toward a longer download so prefetch
    /// fires early); otherwise falls back to the raw-duration statistic (local preprocess).
    /// </summary>
    private double EstimatePrefetchSeconds()
    {
        if (_downloadBytesPerSecond.Count > 0 && _downloadBytes.Count > 0)
        {
            double slowBandwidth = _downloadBytesPerSecond.Quantile(0.1);
            double medianBytes = _downloadBytes.Median;
            if (slowBandwidth > 0 && double.IsFinite(slowBandwidth) && double.IsFinite(medianBytes))
            {
                return medianBytes / slowBandwidth;
            }
        }

        return _prefetchSeconds.Quantile(0.9);
    }

    /// <summary>
    /// Estimated download time for a task of the given size, using the slow-bandwidth (P10)
    /// estimate so a slow link errs toward a longer (earlier-triggered) download.
    /// </summary>
    public double EstimateDownloadSeconds(long bytes)
    {
        double slowBandwidth = _downloadBytesPerSecond.Quantile(0.1);
        if (slowBandwidth > 0 && double.IsFinite(slowBandwidth))
        {
            return bytes / slowBandwidth;
        }

        return _prefetchSeconds.Quantile(0.9);
    }

    /// <summary>
    /// Whether a task of the given size should be claimed now rather than deferred. Returns true
    /// when the download cannot fit inside the remaining upscaling work (so starting immediately
    /// minimizes GPU idle), and true when there is not enough history to estimate. Returns false
    /// when the download is fast enough to fit comfortably, so the worker can wait and avoid
    /// holding the task while its GPU is still busy (leaving it available to faster workers).
    /// </summary>
    public bool ShouldClaim(long bytes, int remainingPages)
    {
        if (remainingPages <= 0)
        {
            return true;
        }

        double downloadSeconds = EstimateDownloadSeconds(bytes);
        if (!double.IsFinite(downloadSeconds) || downloadSeconds <= 0 || _perPageSeconds.Count == 0)
        {
            return true;
        }

        double remainingSeconds = remainingPages * _perPageSeconds.P95Upper;
        return downloadSeconds >= remainingSeconds;
    }
}
