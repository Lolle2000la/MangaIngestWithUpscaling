using MangaIngestWithUpscaling.Shared.Services.Analysis;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

public class DetectServerClientTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void StdinEncoding_DoesNotEmitABom()
    {
        // The resident detection server does json.loads(line), which rejects a leading BOM; a BOM
        // would make every request fail to parse and stall for the full request timeout.
        Assert.Empty(DetectServerClient.StdinEncoding.GetPreamble());

        // The encoding must still round-trip non-ASCII paths.
        byte[] bytes = DetectServerClient.StdinEncoding.GetBytes("{\"path\":\"ページ.png\"}");
        Assert.Equal(
            "{\"path\":\"ページ.png\"}",
            DetectServerClient.StdinEncoding.GetString(bytes)
        );
    }
}
