using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

/// <summary>
/// The CLI fallback must not swallow cancellation as a per-image detection failure: the streamed
/// detection loop uploads whatever result it is handed, so a cancelled page would be persisted as
/// "detection failed" instead of stopping the chapter.
/// </summary>
[Collection("DetectServerClientLayout")]
public class SplitDetectionServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("split_detect_cli").FullName;
    private readonly string _previousRoot = SplitDetectionLayout.Root;

    public SplitDetectionServiceTests()
    {
        SplitDetectionLayout.Root = _root;
        Directory.CreateDirectory(Path.GetDirectoryName(SplitDetectionLayout.ScriptPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(SplitDetectionLayout.CheckpointPath)!);
        File.WriteAllText(SplitDetectionLayout.ScriptPath, "print('{}')");
        File.WriteAllText(SplitDetectionLayout.CheckpointPath, "model");
        File.WriteAllText(SplitDetectionLayout.ConfigPath, "{}");
    }

    public void Dispose()
    {
        SplitDetectionLayout.Root = _previousRoot;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenTheCliIsCancelled_RethrowsInsteadOfReturningAnErrorResult()
    {
        var detectServer = Substitute.For<IDetectServerClient>();
        detectServer
            .DetectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<SplitDetectionResult>>(_ =>
                throw new DetectServerUnavailableException("resident server down")
            );

        var python = Substitute.For<IPythonService>();
        python
            .RunPythonScript(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken?>(),
                Arg.Any<TimeSpan?>()
            )
            .Returns<Task<string>>(_ => throw new OperationCanceledException());

        var service = new SplitDetectionService(
            python,
            Substitute.For<IMangaJaNaiWorkerClient>(),
            detectServer,
            Options.Create(new UpscalerConfig()),
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>()
        );

        string image = Path.Combine(_root, "page.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }
}
