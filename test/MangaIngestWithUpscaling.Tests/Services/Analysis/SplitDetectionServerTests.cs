using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitDetectionServerTests
{
    private readonly IPythonService _pythonService = Substitute.For<IPythonService>();
    private readonly IMangaJaNaiWorkerClient _workerClient =
        Substitute.For<IMangaJaNaiWorkerClient>();
    private readonly IDetectServerClient _detectServer = Substitute.For<IDetectServerClient>();
    private readonly SplitDetectionService _service;

    public SplitDetectionServerTests()
    {
        _service = new SplitDetectionService(
            _pythonService,
            _workerClient,
            _detectServer,
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<SplitDetectionService>>(),
            Substitute.For<IStringLocalizer<SplitDetectionService>>()
        );
    }

    private static string CreateTempImage()
    {
        string image = Path.Combine(Path.GetTempPath(), $"detect_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(image, [1, 2, 3]);
        return image;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_UsesResidentServerForSingleImage()
    {
        string image = CreateTempImage();
        try
        {
            var expected = new SplitDetectionResult { ImagePath = image, Count = 0 };
            _detectServer.DetectAsync(image, Arg.Any<CancellationToken>()).Returns(expected);

            var results = await _service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Single(results);
            Assert.Same(expected, results[0]);
            await _detectServer.Received(1).DetectAsync(image, Arg.Any<CancellationToken>());
            // The resident server was used, so the per-image CLI must not be invoked.
            await _pythonService
                .DidNotReceiveWithAnyArgs()
                .RunPythonScript(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ReturnsServerErrorResultWithoutFallingBack()
    {
        string image = CreateTempImage();
        try
        {
            var expected = new SplitDetectionResult
            {
                ImagePath = image,
                Error = "inference failed",
            };
            _detectServer.DetectAsync(image, Arg.Any<CancellationToken>()).Returns(expected);

            var results = await _service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Single(results);
            Assert.Equal("inference failed", results[0].Error);
            await _pythonService
                .DidNotReceiveWithAnyArgs()
                .RunPythonScript(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_FallsBackToTheCliWhenTheServerIsUnavailable()
    {
        string image = CreateTempImage();

        // Point the layout at a temp root so the test does not write into the real install layout.
        string previousRoot = SplitDetectionLayout.Root;
        string root = Directory.CreateTempSubdirectory("detect_layout").FullName;
        SplitDetectionLayout.Root = root;
        string script = SplitDetectionLayout.ScriptPath;
        string checkpoint = SplitDetectionLayout.CheckpointPath;
        string config = SplitDetectionLayout.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        File.WriteAllText(script, "pass");
        File.WriteAllText(checkpoint, "model");
        File.WriteAllText(config, "{}");

        try
        {
            _detectServer
                .DetectAsync(image, Arg.Any<CancellationToken>())
                .Returns<SplitDetectionResult>(_ =>
                    throw new DetectServerUnavailableException("no server")
                );
            _pythonService
                .RunPythonScript(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken?>(),
                    Arg.Any<TimeSpan?>()
                )
                .Returns($"{{\"image\":{JsonSerializer.Serialize(image)},\"count\":0}}");

            var results = await _service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Single(results);
            Assert.Equal(image, results[0].ImagePath);
            // The unavailable resident server must have driven the per-image CLI, not been swallowed.
            await _pythonService
                .Received(1)
                .RunPythonScript(
                    script,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken?>(),
                    Arg.Any<TimeSpan?>()
                );
        }
        finally
        {
            SplitDetectionLayout.Root = previousRoot;
            File.Delete(image);
            Directory.Delete(root, true);
        }
    }
}
