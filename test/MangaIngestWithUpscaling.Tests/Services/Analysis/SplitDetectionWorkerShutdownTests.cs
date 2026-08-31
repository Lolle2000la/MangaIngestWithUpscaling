using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitDetectionWorkerShutdownTests
{
    private readonly IPythonService _pythonService;
    private readonly IMangaJaNaiWorkerClient _workerClient;
    private readonly ILogger<SplitDetectionService> _logger;
    private readonly SplitDetectionService _service;

    public SplitDetectionWorkerShutdownTests()
    {
        _pythonService = Substitute.For<IPythonService>();
        _workerClient = Substitute.For<IMangaJaNaiWorkerClient>();
        _logger = Substitute.For<ILogger<SplitDetectionService>>();
        _service = new SplitDetectionService(
            _pythonService,
            _workerClient,
            _logger,
            Substitute.For<IStringLocalizer<SplitDetectionService>>()
        );
    }

    [Fact]
    public async Task DetectSplitsAsync_ShutsDownPersistentWorkerBeforeDetecting()
    {
        // The persistent upscaling worker holds most of the VRAM until its idle timeout,
        // so detection must release it first even when detection itself cannot proceed.
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.DetectSplitsAsync(
                "nonexistent-path",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        _ = _workerClient.Received(1).ShutdownWorkerAsync(Arg.Any<CancellationToken>());
    }
}
