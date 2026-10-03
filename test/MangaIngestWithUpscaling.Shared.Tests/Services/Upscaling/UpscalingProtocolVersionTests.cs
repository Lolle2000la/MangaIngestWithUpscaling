using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class UpscalingProtocolVersionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void IsSupported_AcceptsTheSupportedRangeAndRejectsOutsideIt()
    {
        Assert.True(UpscalingProtocolVersion.IsSupported(UpscalingProtocolVersion.MinSupported));
        Assert.True(UpscalingProtocolVersion.IsSupported(UpscalingProtocolVersion.Current));
        Assert.False(
            UpscalingProtocolVersion.IsSupported(UpscalingProtocolVersion.MinSupported - 1)
        );
        Assert.False(UpscalingProtocolVersion.IsSupported(UpscalingProtocolVersion.Current + 1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsCompatible_AcceptsOverlappingRangesAndRejectsDisjointOnes()
    {
        // A newer-but-compatible peer: its Current is above ours but its Min reaches into our range.
        Assert.True(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current + 1,
                UpscalingProtocolVersion.Current
            )
        );
        // Exactly our range.
        Assert.True(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current,
                UpscalingProtocolVersion.MinSupported
            )
        );
        // A peer whose whole range is below ours (an old worker that dropped our version).
        Assert.False(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.MinSupported - 1,
                UpscalingProtocolVersion.MinSupported - 1
            )
        );
        // A peer that only speaks versions above ours.
        Assert.False(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current + 1,
                UpscalingProtocolVersion.Current + 1
            )
        );
    }
}
