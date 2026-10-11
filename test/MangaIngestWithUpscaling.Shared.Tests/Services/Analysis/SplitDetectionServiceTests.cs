using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NetVips;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

[Collection("SplitDetectionLayout")]
public class SplitDetectionServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("split_detect_cli").FullName;
    private readonly string _previousRoot = SplitDetectionLayout.Root;

    public SplitDetectionServiceTests()
    {
        SplitDetectionLayout.Root = _root;
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
    public async Task DetectSplitsAsync_WhenCancelledMidInference_ThrowsOperationCanceledException()
    {
        // Cancellation is observed from inside the operation, once the page has been decoded and the
        // service reaches the session factory. The previous, pre-cancelled form threw at the check
        // before the try, so it never exercised `catch (OperationCanceledException) { throw; }`:
        // deleting that catch would have left it green. Here the cancel lands after the try begins,
        // so without the rethrow it would be swallowed by the generic catch and returned as an error
        // result instead.
        string modelsDir = Path.Combine(_root, "cancel_models");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllBytes(Path.Combine(modelsDir, "page_break_detector.onnx"), [0]);

        var factory = Substitute.For<IOnnxSessionFactory>();
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();

        using var cts = new CancellationTokenSource();
        factory
            .GetOrCreateSession(Arg.Any<string>())
            .Returns(_ =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return null!;
            });

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer,
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DetectSplitsAsync(
                WriteDecodableImage("cancel.png"),
                cancellationToken: cts.Token
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenInferenceFails_InvalidatesTheCachedSession()
    {
        // A model that loads and runs, but whose outputs are not the detector's, so reading the
        // result throws once Run has returned. The failure type does not matter to the fix: what
        // matters is that the cached session is dropped instead of being reused, and failing again,
        // for every later page — the factory is only allowed to cache one.
        string modelsDir = Path.Combine(_root, "failing_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(
            modelsDir,
            scale: 2,
            fp16: false,
            fileName: "page_break_detector.onnx"
        );

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        List<SplitDetectionResult> results = await service.DetectSplitsAsync(
            WriteDecodableImage("failure.png"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        SplitDetectionResult result = Assert.Single(results);
        Assert.NotNull(result.Error);
        factory.Received(1).InvalidateSession(modelPath);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var factory = Substitute.For<IOnnxSessionFactory>();
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer
        );

        string image = Path.Combine(_root, "non_existent.png");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenModelDoesNotExist_ThrowsFileNotFoundException()
    {
        var factory = Substitute.For<IOnnxSessionFactory>();
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = Path.Combine(_root, "empty_models") }
        );

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer,
            config
        );

        string image = Path.Combine(_root, "sample.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    /// <summary>
    /// Writes a page NetVips can decode. The service decodes the page before it reaches the session
    /// factory, so a test that needs the operation to get past the pre-flight checks has to hand it
    /// a real image, not a few arbitrary bytes.
    /// </summary>
    private string WriteDecodableImage(string fileName)
    {
        // 768 wide so the detector's fixed-width resize is a no-op, and short so the packed tensor
        // stays small. The content is irrelevant: none of these tests reach the model output.
        const int width = 768;
        const int height = 4;
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, (byte)128);

        string path = Path.Combine(_root, fileName);
        using var image = Image.NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
        return path;
    }
}

/// <summary>
/// Serializes tests that mutate the process-wide <see cref="SplitDetectionLayout.Root" />.
/// </summary>
[CollectionDefinition("SplitDetectionLayout", DisableParallelization = true)]
public class SplitDetectionLayoutCollection;
