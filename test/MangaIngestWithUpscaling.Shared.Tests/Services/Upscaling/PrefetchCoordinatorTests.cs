using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class PrefetchCoordinatorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void OnProgress_WhenFinalizing_SignalsOnce()
    {
        var coordinator = new PrefetchCoordinator();
        Assert.True(coordinator.OnProgress(null, null, "finalizing"));
        Assert.False(coordinator.OnProgress(null, null, "finalizing")); // already signaled this job
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnProgress_WithoutFinalizing_NeedsPageCounts()
    {
        var coordinator = new PrefetchCoordinator();
        Assert.False(coordinator.OnProgress(null, null));
    }
}
