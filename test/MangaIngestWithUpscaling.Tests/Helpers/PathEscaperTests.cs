using MangaIngestWithUpscaling.Helpers;

namespace MangaIngestWithUpscaling.Tests.Helpers;

public class PathEscaperTests
{
    [Theory]
    [InlineData(".", "%2E")]
    [InlineData("..", "%2E%2E")]
    [InlineData("...", "%2E%2E%2E")]
    [InlineData(".. ", "%2E%2E")]
    [InlineData("", "%20")]
    [InlineData(" ", "%20")]
    [InlineData("Normal Title", "Normal Title")]
    [InlineData("Oshi no Ko...", "Oshi no Ko...")]
    [InlineData("a/b", "a%2Fb")]
    public void EscapeDirectoryName_ShouldNeutralizeTraversalSegments(string input, string expected)
    {
        Assert.Equal(expected, PathEscaper.EscapeDirectoryName(input));
    }
}
