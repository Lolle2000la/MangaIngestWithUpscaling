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

    [Fact]
    [Trait("Category", "Unit")]
    public void MinSupported_EqualsCurrentUntilNegotiationIsImplemented()
    {
        // Tripwire: with no wire negotiation, both sides speak their own Current, so the range-overlap
        // handshake is only safe while MinSupported == Current. Decoupling them (a partial-rollout
        // bump) would let a newer side apply new behavior unilaterally; implement negotiation first.
        Assert.Equal(UpscalingProtocolVersion.Current, UpscalingProtocolVersion.MinSupported);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Negotiated_IsTheUpperBoundOfTheOverlap()
    {
        Assert.Equal(
            UpscalingProtocolVersion.Current,
            UpscalingProtocolVersion.Negotiated(UpscalingProtocolVersion.Current + 1)
        );
        Assert.Equal(
            UpscalingProtocolVersion.MinSupported,
            UpscalingProtocolVersion.Negotiated(UpscalingProtocolVersion.MinSupported)
        );
    }
}
