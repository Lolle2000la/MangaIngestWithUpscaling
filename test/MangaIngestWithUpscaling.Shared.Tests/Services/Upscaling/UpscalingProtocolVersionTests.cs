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
    public void IsCompatible_RequiresThePeerToSpeakOurCurrentVersion()
    {
        // A peer whose range spans our Current is accepted.
        Assert.True(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current,
                UpscalingProtocolVersion.MinSupported
            )
        );
        Assert.True(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current,
                UpscalingProtocolVersion.Current
            )
        );

        // A newer peer is rejected until the two sides negotiate a shared version: it would keep using
        // its own Current while this build uses ours.
        Assert.False(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current + 1,
                UpscalingProtocolVersion.Current
            )
        );
        Assert.False(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.Current + 1,
                UpscalingProtocolVersion.Current + 1
            )
        );

        // A peer whose whole range is below ours (an old worker that dropped our version).
        Assert.False(
            UpscalingProtocolVersion.IsCompatible(
                UpscalingProtocolVersion.MinSupported - 1,
                UpscalingProtocolVersion.MinSupported - 1
            )
        );
    }
}
