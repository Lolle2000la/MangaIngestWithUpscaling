using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class PrefetchPredictorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void OnlineStats_Median_InterpolatesBetweenOrderStatistics()
    {
        var stats = new OnlineStats();
        stats.Add(1);
        stats.Add(2);
        stats.Add(3);
        stats.Add(4);

        // sorted [1,2,3,4]; rank for p=0.5 is 1.5 -> between 2 and 3.
        Assert.Equal(2.5, stats.Median, 10);
        Assert.Equal(1, stats.Quantile(0.0), 10);
        Assert.Equal(4, stats.Quantile(1.0), 10);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnlineStats_Quantile_WithNoSamples_IsNaN()
    {
        var stats = new OnlineStats();
        Assert.True(double.IsNaN(stats.Quantile(0.5)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldClaim_WhenDownloadOutlastsRemainingWork_ReturnsTrue()
    {
        var predictor = new PrefetchPredictor();
        // 100 bytes in 100s => 1 byte/s bandwidth.
        predictor.RecordDownload(100, TimeSpan.FromSeconds(100));
        predictor.RecordPerPage(10);

        // 1000 bytes at 1 byte/s = 1000s download vs 5 pages * 10s = 50s remaining.
        Assert.True(predictor.ShouldClaim(1000, 5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldClaim_WhenDownloadFitsInRemainingWork_ReturnsFalse()
    {
        var predictor = new PrefetchPredictor();
        // 1000 bytes in 1s => 1000 bytes/s bandwidth.
        predictor.RecordDownload(1000, TimeSpan.FromSeconds(1));
        predictor.RecordPerPage(10);

        // 100 bytes at 1000 bytes/s = 0.1s download vs 5 pages * 10s = 50s remaining.
        Assert.False(predictor.ShouldClaim(100, 5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldClaim_WithoutHistory_ReturnsTrue()
    {
        var predictor = new PrefetchPredictor();
        Assert.True(predictor.ShouldClaim(1000, 5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldClaim_WithPerPageHistoryButNoBandwidth_ReturnsTrue()
    {
        var predictor = new PrefetchPredictor();
        // Per-page samples exist but no download/bandwidth samples: the download-time estimate
        // is NaN, which must not be treated as "fits in remaining work" (i.e. defer).
        predictor.RecordPerPage(10);
        Assert.True(predictor.ShouldClaim(1000, 5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldClaim_WhenNoPagesRemain_ReturnsTrue()
    {
        var predictor = new PrefetchPredictor();
        predictor.RecordDownload(1000, TimeSpan.FromSeconds(1));
        predictor.RecordPerPage(10);
        Assert.True(predictor.ShouldClaim(1_000_000, 0));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EstimateDownloadSeconds_UsesSlowBandwidthQuantile()
    {
        var predictor = new PrefetchPredictor();
        // Mostly fast (1000 bytes/s), one slow sample (10 bytes/s). The P10 slow-bandwidth
        // estimate should yield a longer download prediction than the mean (~800 bytes/s)
        // would, so a 1000-byte download is predicted at >1s.
        predictor.RecordDownload(1000, TimeSpan.FromSeconds(1));
        predictor.RecordDownload(1000, TimeSpan.FromSeconds(1));
        predictor.RecordDownload(1000, TimeSpan.FromSeconds(1));
        predictor.RecordDownload(1000, TimeSpan.FromSeconds(1));
        predictor.RecordDownload(100, TimeSpan.FromSeconds(10));

        double estimate = predictor.EstimateDownloadSeconds(1000);
        Assert.True(estimate > 1.0, $"expected a conservative (slow) estimate, got {estimate}s");
    }
}
