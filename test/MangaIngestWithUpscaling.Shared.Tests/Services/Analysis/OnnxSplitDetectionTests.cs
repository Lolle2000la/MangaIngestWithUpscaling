using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

public class OnnxSplitDetectionTests
{
    private static readonly string TestImagePath = ResolveTestImagePath();

    private static string ResolveTestImagePath()
    {
        string testDataPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "manga splits visualized.png"
        );
        if (File.Exists(testDataPath))
        {
            return testDataPath;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "backend",
            "src",
            "manga-vert-split-nn",
            "assets",
            "manga splits visualized.png"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectSplitsAsync_OnSampleImage_ExecutesSuccessfully()
    {
        string modelPath = SplitDetectionLayout.ResolveModelPath();
        if (!File.Exists(TestImagePath) || !File.Exists(modelPath))
        {
            return;
        }

        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer,
            config
        );

        List<SplitDetectionResult> results = await service.DetectSplitsAsync(
            TestImagePath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Single(results);
        var result = results[0];
        Assert.Null(result.Error);
        Assert.Equal(1330, result.OriginalWidth);
        Assert.Equal(1058, result.OriginalHeight);
        Assert.NotNull(result.Splits);
    }
}
