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
}
