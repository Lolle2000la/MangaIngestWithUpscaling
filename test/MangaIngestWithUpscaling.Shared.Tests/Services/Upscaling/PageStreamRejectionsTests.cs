using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class PageStreamRejectionsTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(PageStreamDisposition.Accepted, false)]
    [InlineData(PageStreamDisposition.Retry, false)]
    [InlineData(PageStreamDisposition.Terminal, true)]
    public void ToWireTerminal_OnlyTerminalIsTerminal(
        PageStreamDisposition disposition,
        bool expected
    )
    {
        // The wire contract: only Terminal sets terminal=true, so Retry (and Accepted) keeps the spool.
        Assert.Equal(expected, PageStreamRejections.ToWireTerminal(disposition));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false, false, PageStreamDisposition.Retry)]
    [InlineData(true, false, PageStreamDisposition.Terminal)]
    [InlineData(false, true, PageStreamDisposition.Terminal)]
    [InlineData(true, true, PageStreamDisposition.Terminal)]
    public void ForResolution_CorruptOrTerminalTaskIsTerminal(
        bool taskTerminal,
        bool corrupt,
        PageStreamDisposition expected
    )
    {
        Assert.Equal(expected, PageStreamRejections.ForResolution(taskTerminal, corrupt));
    }
}
