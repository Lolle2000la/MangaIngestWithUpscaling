using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class MangaJaNaiWorkerClientTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(CompressionFormat.Webp, "webp")]
    [InlineData(CompressionFormat.Png, "png")]
    [InlineData(CompressionFormat.Jpg, "jpeg")]
    [InlineData(CompressionFormat.Avif, "avif")]
    public void ToFormatString_MapsCompressionFormatToWorkerFormat(
        CompressionFormat format,
        string expected
    )
    {
        Assert.Equal(expected, MangaJaNaiWorkerClient.ToFormatString(format));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildJobLine_ProducesWellFormedJobRequest()
    {
        var request = new UpscaleJobRequest
        {
            Id = "job-1",
            InputPath = "/data/ch1.cbz",
            OutputFolder = "/out",
            OutputFilename = "chapter-1",
            Format = CompressionFormat.Webp,
            Scale = ScaleFactor.TwoX,
            Overwrite = true,
        };

        string line = MangaJaNaiWorkerClient.BuildJobLine(request);

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;

        Assert.Equal("job", root.GetProperty("type").GetString());
        Assert.Equal("job-1", root.GetProperty("id").GetString());
        Assert.Equal("/data/ch1.cbz", root.GetProperty("input").GetProperty("path").GetString());

        JsonElement output = root.GetProperty("output");
        Assert.Equal("/out", output.GetProperty("folder").GetString());
        Assert.Equal("chapter-1", output.GetProperty("filename").GetString());
        Assert.Equal("webp", output.GetProperty("format").GetString());
        Assert.True(output.GetProperty("overwrite").GetBoolean());

        JsonElement options = root.GetProperty("options");
        Assert.Equal(2, options.GetProperty("scale").GetInt32());
    }
}
