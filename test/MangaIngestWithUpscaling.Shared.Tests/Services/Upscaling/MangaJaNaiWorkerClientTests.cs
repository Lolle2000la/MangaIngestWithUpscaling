using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

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

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(CompressionFormat.Webp, ".webp")]
    [InlineData(CompressionFormat.Png, ".png")]
    [InlineData(CompressionFormat.Jpg, ".jpg")]
    [InlineData(CompressionFormat.Avif, ".avif")]
    public void ToExtension_MapsCompressionFormatToExtension(
        CompressionFormat format,
        string expected
    )
    {
        Assert.Equal(expected, MangaJaNaiWorkerClient.ToExtension(format));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunJobAsync_WithCbzRequest_DelegatesToUpscaleEngine()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var client = new MangaJaNaiWorkerClient(
            engine,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

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

        var result = await client.RunJobAsync(
            request,
            null,
            TestContext.Current.CancellationToken,
            null
        );

        Assert.Equal("job-1", result.Id);
        Assert.Equal("success", result.Status);
        Assert.Single(result.Files);

        await engine
            .Received(1)
            .UpscaleCbzAsync(
                "/data/ch1.cbz",
                Path.Combine("/out", "chapter-1.cbz"),
                2,
                CompressionFormat.Webp,
                null,
                null,
                TestContext.Current.CancellationToken
            );
    }
}
