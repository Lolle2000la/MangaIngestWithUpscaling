using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitDetectionWorkerShutdownTests
{
    private readonly IPythonService _pythonService;
    private readonly IMangaJaNaiWorkerClient _workerClient;
    private readonly UpscalerConfig _config;
    private readonly SplitDetectionService _service;

    public SplitDetectionWorkerShutdownTests()
    {
        _pythonService = Substitute.For<IPythonService>();
        _workerClient = Substitute.For<IMangaJaNaiWorkerClient>();
        _config = new UpscalerConfig();
        _service = new SplitDetectionService(
            _pythonService,
            _workerClient,
            Options.Create(_config),
            Substitute.For<ILogger<SplitDetectionService>>(),
            Substitute.For<IStringLocalizer<SplitDetectionService>>()
        );
    }

    [Fact]
    public async Task DetectSplitsAsync_FreesWorkerVramBeforeDetecting()
    {
        // The persistent upscaling worker holds cached VRAM, so it must be released
        // before detection even when detection itself cannot proceed.
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.DetectSplitsAsync(
                "nonexistent-path",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        _ = _workerClient.Received(1).ReleaseGpuCacheAsync(Arg.Any<CancellationToken>());
        await _workerClient.DidNotReceive().ShutdownWorkerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectSplitsAsync_ShutsWorkerDownWhenConfigured()
    {
        // Very VRAM-limited setups opt into a full teardown so an idle worker cannot
        // starve detection.
        _config.ShutdownWorkerBeforeSplitDetection = true;

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.DetectSplitsAsync(
                "nonexistent-path",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        await _workerClient.Received(1).ShutdownWorkerAsync(Arg.Any<CancellationToken>());
        _ = _workerClient.DidNotReceive().ReleaseGpuCacheAsync(Arg.Any<CancellationToken>());
    }
}
