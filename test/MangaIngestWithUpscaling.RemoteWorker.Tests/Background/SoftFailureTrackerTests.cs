using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

public class SoftFailureTrackerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Increment_CountsConsecutiveFailuresPerTask()
    {
        var tracker = new SoftFailureTracker();

        Assert.Equal(1, tracker.Increment(1));
        Assert.Equal(2, tracker.Increment(1));
        Assert.Equal(1, tracker.Increment(2));
        Assert.Equal(3, tracker.Increment(1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Reset_ForgetsOnlyThatTask()
    {
        var tracker = new SoftFailureTracker();
        tracker.Increment(1);
        tracker.Increment(2);

        tracker.Reset(1);

        Assert.Equal(1, tracker.Increment(1));
        Assert.Equal(2, tracker.Increment(2));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CapFor_UsesTheLargerCapForRestarts()
    {
        // A restart is not terminalised at the small soft-failure cap; a transient/permanent failure
        // is.
        Assert.Equal(
            SoftFailureTracker.MaxConsecutiveSoftFailures,
            SoftFailureTracker.CapFor(StreamingFailureKind.Transient)
        );
        Assert.Equal(
            SoftFailureTracker.MaxConsecutiveSoftFailures,
            SoftFailureTracker.CapFor(StreamingFailureKind.Permanent)
        );
        Assert.Equal(
            SoftFailureTracker.MaxConsecutiveRestarts,
            SoftFailureTracker.CapFor(StreamingFailureKind.Restart)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Caps_PreserveTheExistingValues()
    {
        Assert.Equal(5, SoftFailureTracker.MaxConsecutiveSoftFailures);
        Assert.Equal(100, SoftFailureTracker.MaxConsecutiveRestarts);
    }
}
