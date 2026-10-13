using System.Diagnostics;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
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
    public async Task DetectSplitsAsync_WhenDetectorOutputIsNonFinite_IsRethrownForRetry()
    {
        // A mis-executing execution provider returns NaN instead of failing, and every comparison
        // against NaN is false — so without the guard the mask is all-false and the page is reported
        // as a SUCCESSFUL detection with zero splits. That silent miss is still rejected, but the
        // rejection is now rethrown rather than turned into a per-page error: an error finding reaches
        // SplitProcessingService and, when every page carries one, calls SetFailedAsync — the chapter
        // failed for good on what the guard's own comment calls a mis-executing provider, and the
        // detector has no second model to fall back to. Rethrown, the worker's infrastructure
        // classifier requeues the task, and the session that produced the NaN is dropped so the retry
        // re-probes with a fresh one.
        string modelsDir = Path.Combine(_root, "nonfinite_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx",
            nonFiniteProbabilities: true
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

        // A black page makes the row mean 0, so the injected division is 0/0 — a NaN in
        // probabilities with an all-false peak_mask, exactly the silent-zero-split shape.
        await Assert.ThrowsAsync<NonFiniteModelOutputException>(() =>
            service.DetectSplitsAsync(
                WriteDecodableImage("nonfinite.png", fill: 0),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        factory.Received(1).InvalidateSession(modelPath);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_TallPage_IsOneInferenceOverTheWholeResizedHeight()
    {
        // A page taller than one inference window is detected in several windows, and the result has to
        // be the same as a whole-page pass. The window's context margin is what makes that hold (see
        // DetectorWindowMargin); this pins the shape of the result: one row per resized row, with the
        // 100-row edge margins applied to the page rather than to each window. A window whose output
        // were handed back as-is would fail the per-window geometry check instead.
        string modelsDir = Path.Combine(_root, "tall_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
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

        // 768 wide, so the resize is a no-op and the resized height is the source height, several
        // times the reference's chunk size.
        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("tall.png", fill: 200, height: 3000),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(3000, result.OriginalHeight);
        Assert.Equal(768, result.OriginalWidth);

        // Every row above the margin carries the synthetic peak, so the count also proves the whole
        // height was seen: a single 3072-row chunk returning early would have produced a different
        // count, and a shorter output would have failed the geometry check instead.
        Assert.Equal(3000 - 2 * 100, result.Count);
        Assert.Equal(2999 - 100, result.Splits[^1].YOriginal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ImageWithTransparency_DropsTheAlphaInsteadOfCompositingItAway()
    {
        // The reference calls PIL's convert("RGB"), which keeps the colour stored under a transparent
        // pixel and merely discards the alpha band. libvips' Flatten() composites onto black instead,
        // so a transparent region turns into a black band — and since the detector's probability is a
        // row mean, that moves rows across the peak threshold (measured with the shipped model: an LA
        // page with a transparent row yields a different peak set).
        //
        // The synthetic detector's probability is the row mean, so the split rows are a direct read-out
        // of the pixel values after alpha handling: the band below is transparent and must still be
        // detected, because the colour under it is 200/255 rather than 0.
        string modelsDir = Path.Combine(_root, "alpha_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
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

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteTransparentImage(
                    "transparent.png",
                    height: 400,
                    transparentFrom: 150,
                    rows: 100
                ),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        // Every row between the 100-row edge margins carries the synthetic peak, transparent or not:
        // before the alpha fix the transparent band was composited to black, which dropped exactly
        // those 100 rows from the mask.
        Assert.Equal(400 - 2 * 100, result.Count);
        Assert.Equal(100, result.Splits[0].YOriginal);

        // A row inside the transparent band reads back the colour stored under the alpha (200/255)
        // rather than the 0.0 that compositing onto black would produce.
        DetectedSplit transparentRow = result.Splits.Single(s => s.YOriginal == 150);
        Assert.Equal(200f / 255f, transparentRow.Confidence, 2);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("64MiB")]
    [InlineData("4GiB")]
    public async Task DetectSplitsAsync_PageTallerThanAWindow_AssemblesEveryRowFromItsOwnWindow(
        string budget
    )
    {
        // A page taller than the inference window is detected in several windows, and every row has to
        // come back from the window that was trusted for it. The synthetic detector's probability is
        // the row mean, so filling row y with (128 + y % 128) makes each row's value identify where it
        // was read from: a window offset or margin mistake shifts the whole assembly and every
        // confidence after the first window boundary comes out wrong. The budget is varied to pin the
        // other half of the contract — 64MiB forces windows, 4GiB buys the whole-page pass, and the
        // detected splits have to be identical either way, because the margin (not the chunk) is what
        // makes a window exact.
        string modelsDir = Path.Combine(_root, "window_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx"
        );

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(
                new UpscalerConfig { ModelsDirectory = modelsDir, MemoryBudgetBytes = budget }
            )
        );

        // Windows at whatever chunk the budget buys (512-row chunks for 64MiB, one whole-page pass
        // for 4GiB), the last one partial; 100 rows are dropped at each end.
        const int height = 7000;
        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteGradientImage("gradient.png", height),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        var expected = new List<(int YOriginal, double Confidence)>();
        for (int y = 100; y < height - 100; y++)
        {
            expected.Add((y, Math.Round((128 + (y % 128)) / 255f, 4)));
        }

        Assert.Equal(expected, result.Splits.Select(s => (s.YOriginal, s.Confidence)).ToList());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ContextDependentDetector_WindowedResultMatchesTheWholePagePass()
    {
        // Every other window test uses a detector whose score is a per-row reduction, so a window
        // reproduces the whole-page rows no matter what context margin it carries — those tests cannot
        // notice a margin below the model's receptive field. This stub convolves the row mean over
        // +-1431 rows (the real detector's field, so the 1536-row DetectorWindowMargin covers it), so a
        // row only scores from the context its field reaches. A single bright row makes the whole-page
        // mask a simple band [bright - 1431, bright + 1431]; a window with too little margin loses that
        // row's contribution near its boundaries and the two passes disagree.
        const int contextRadius = 1431;
        const int height = 7000;
        const int brightRow = 1500;

        string modelsDir = Path.Combine(_root, "context_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx",
            contextRadius: contextRadius
        );
        string imagePath = WriteSingleBrightRowImage("context.png", height, brightRow);

        SplitDetectionResult windowed = await DetectWithBudget(
            modelsDir,
            modelPath,
            imagePath,
            "64MiB"
        );
        SplitDetectionResult wholePage = await DetectWithBudget(
            modelsDir,
            modelPath,
            imagePath,
            "4GiB"
        );

        // The band has to exist, or the comparison below would pass vacuously for an empty result.
        Assert.NotEmpty(wholePage.Splits);
        Assert.All(
            wholePage.Splits,
            split =>
                Assert.InRange(
                    split.YOriginal,
                    brightRow - contextRadius,
                    brightRow + contextRadius
                )
        );

        // The 64 MiB budget splits the 7000 rows into 512-row windows, the 4 GiB one runs the whole
        // page in a single pass. With the 1536-row margin they must agree row for row, confidence and
        // all; a margin below the 1431-row field cannot.
        Assert.Equal(
            wholePage.Splits.Select(s => (s.YOriginal, s.Confidence)).ToList(),
            windowed.Splits.Select(s => (s.YOriginal, s.Confidence)).ToList()
        );
    }

    private async Task<SplitDetectionResult> DetectWithBudget(
        string modelsDir,
        string modelPath,
        string imagePath,
        string budget
    )
    {
        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(
                new UpscalerConfig { ModelsDirectory = modelsDir, MemoryBudgetBytes = budget }
            )
        );

        return Assert.Single(
            await service.DetectSplitsAsync(
                imagePath,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    private string WriteSingleBrightRowImage(string fileName, int height, int brightRow)
    {
        // 768 wide so the detector's resize is a no-op, otherwise black. One bright row keeps the
        // context-dependent stub's mask a simple band, so a missing margin shows up as a band row the
        // windowed pass lost rather than as a change in a value that is over the threshold anyway.
        const int width = 768;
        var pixels = new byte[width * height * 3];
        for (int x = 0; x < width; x++)
        {
            int i = ((brightRow * width) + x) * 3;
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
        }

        string path = Path.Combine(_root, fileName);
        using var image = Image.NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
        return path;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_GrayscalePage_IsReplicatedToThreeChannelsLikeTheReference()
    {
        // The reference feeds the model PIL's convert("RGB"), which replicates the single band of an
        // L image to R=G=B; libvips' Colourspace(Srgb) on a 1-band B_W image does the same (it is the
        // interpretation that decides the branch, not the band count). Pinned here because the two are
        // independent implementations of the same intent and a grayscale page is the common case for
        // manga: a conversion that produced anything else — black, or an error — would be invisible in
        // a colour-only test suite.
        string modelsDir = Path.Combine(_root, "gray_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
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

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteGrayscaleImage("gray.png", fill: 200, height: 400),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(400 - 2 * 100, result.Count);
        // Row mean of the replicated band, i.e. the value survived the grayscale -> sRGB conversion.
        Assert.Equal(200f / 255f, result.Splits[0].Confidence, 2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ForeignModelAtTheDetectorPath_ThrowsAContractError()
    {
        // The model path is resolved by name and accepted if it exists, and the pinned hash only
        // repairs a swapped file while models are verified at startup. An identity graph is what any
        // unrelated .onnx looks like from here: an 'input' that type-checks but no 'probabilities'
        // output. Without the contract check this surfaced as ONNX Runtime's "is not in the metadata"
        // per page; it must name the tensors it found instead.
        string modelsDir = Path.Combine(_root, "foreign_models");
        Directory.CreateDirectory(modelsDir);
        string identityPath = TinyOnnxModel.WriteIdentity(modelsDir, fp16: false);
        string modelPath = Path.Combine(modelsDir, "page_break_detector.onnx");
        File.Move(identityPath, modelPath);

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        // Reported as the page's error rather than thrown: the structural catch already treats "this
        // model cannot run here" as a process-wide verdict, and because that verdict is keyed on the
        // file's length and mtime, replacing the file with the real detector clears it.
        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("foreign.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(result.Error);
        Assert.Contains(
            $"no output named '{SplitDetectionService.ProbabilitiesOutputName}'",
            result.Error
        );
        Assert.Contains("'output'", result.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_DisposedFactory_IsRethrownNotReportedAsAPageError()
    {
        // Shutdown races a page: the factory is torn down while detection runs. Reported as a per-page
        // error that reaches SetFailedAsync, it would fail a chapter that is merely being stopped — so
        // it is excluded from the structural catch and rethrown like a cancellation.
        string modelsDir = Path.Combine(_root, "disposed_models");
        Directory.CreateDirectory(modelsDir);
        TinyOnnxModel.WritePeakMaskDetector(modelsDir, fileName: "page_break_detector.onnx");

        var factory = Substitute.For<IOnnxSessionFactory>();
        factory
            .EnterInferenceScope()
            .Returns(_ => throw new ObjectDisposedException(nameof(OnnxSessionFactory)));

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.DetectSplitsAsync(
                WriteDecodableImage("disposed.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        // A teardown race is not a property of the detector, so it must not be remembered as one.
        factory.DidNotReceive().InvalidateSession(Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenImageDecodeFails_DoesNotInvalidateTheSession()
    {
        // A corrupt page fails during decode, before the service ever asks the factory for a
        // session. Invalidating there would drop a perfectly healthy model and force a rebuild for
        // every remaining page of the chapter, so the invalidation is gated on the failure actually
        // coming out of the session.
        string modelsDir = Path.Combine(_root, "decode_models");
        Directory.CreateDirectory(modelsDir);
        TinyOnnxModel.WritePeakMaskDetector(modelsDir, fileName: "page_break_detector.onnx");

        var factory = Substitute.For<IOnnxSessionFactory>();

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        string corrupt = Path.Combine(_root, "corrupt.png");
        await File.WriteAllTextAsync(
            corrupt,
            "this is not an image",
            TestContext.Current.CancellationToken
        );

        List<SplitDetectionResult> results = await service.DetectSplitsAsync(
            corrupt,
            cancellationToken: TestContext.Current.CancellationToken
        );

        SplitDetectionResult result = Assert.Single(results);
        Assert.NotNull(result.Error);
        factory.DidNotReceive().InvalidateSession(Arg.Any<string>());
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenDetectorFailsStructurally_DoesNotRebuildItForEveryPage()
    {
        // A model that loads and runs but cannot serve as the detector (its outputs are not the
        // detector's) fails structurally: it will fail again on every page. Without a verdict cache
        // the session is rebuilt and re-failed per page of every chapter; the second page must be
        // answered from the cached verdict instead.
        string modelsDir = Path.Combine(_root, "structural_models");
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
        factory.GetEffectiveBackend().Returns(GpuBackend.CPU);

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        SplitDetectionResult first = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("structural-first.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        SplitDetectionResult second = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("structural-second.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(first.Error);
        Assert.NotNull(second.Error);
        // The second page was served from the verdict, not by rebuilding the session.
        factory.Received(1).GetOrCreateSession(modelPath);

        // The verdict is scoped to the resolved backend: a different backend is not disabled by it.
        factory.GetEffectiveBackend().Returns(GpuBackend.WebGPU);
        var otherBackend = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );
        await otherBackend.DetectSplitsAsync(
            WriteDecodableImage("structural-other-backend.png"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        factory.Received(2).GetOrCreateSession(modelPath);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenInferenceHangs_ThrowsATimeoutWithinTheTimeout()
    {
        // session.Run is synchronous and cannot be cancelled, so a hung provider would otherwise
        // park the chapter forever. The service bounds the await and returns an error instead.
        string modelsDir = Path.Combine(_root, "hang_models");
        Directory.CreateDirectory(modelsDir);
        TinyOnnxModel.WritePeakMaskDetector(modelsDir, fileName: "page_break_detector.onnx");

        var real = new OnnxSessionFactory(
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir, UseCPU = true }),
            NullLogger<OnnxSessionFactory>.Instance
        );
        var factory = Substitute.For<IOnnxSessionFactory>();
        using var release = new ManualResetEventSlim(false);
        factory
            .GetOrCreateSession(Arg.Any<string>())
            .Returns(ci =>
            {
                // Never return from session creation until the test lets it, simulating a wedged
                // execution provider.
                release.Wait(TimeSpan.FromSeconds(30));
                return real.GetOrCreateSession(ci.ArgAt<string>(0));
            });
        factory.EnterInferenceScope().Returns(_ => real.EnterInferenceScope());
        factory.GetEffectiveBackend().Returns(GpuBackend.CPU);

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(
                new UpscalerConfig
                {
                    ModelsDirectory = modelsDir,
                    UseCPU = true,
                    UpscaleTimeout = TimeSpan.FromMilliseconds(300),
                }
            )
        );

        try
        {
            var stopwatch = Stopwatch.StartNew();
            // Thrown as a TimeoutException, not returned as an error result: an error finding is
            // uploaded as a normal page finding and ends in SplitProcessingService failing the chapter
            // for good, while a TimeoutException is classified transient (the upscaler path treats a
            // timed-out upscale the same way) and requeues the task with the spool intact.
            await Assert.ThrowsAsync<TimeoutException>(() =>
                service.DetectSplitsAsync(
                    WriteDecodableImage("hang.png"),
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"detection was not bounded; it took {stopwatch.Elapsed}"
            );
        }
        finally
        {
            release.Set();
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(199, 2.0, 100, 99)] // rounds to 100 == origHeight; clamped to the last real row
    [InlineData(100, 2.0, 100, 50)]
    [InlineData(0, 2.0, 100, 0)]
    [InlineData(3, 1.0, 4, 3)]
    [InlineData(0, 0.0, 4, 0)]
    public void MapRowToOriginal_ClampsToTheLastRealRow(
        int row,
        double vscale,
        int origHeight,
        int expected
    )
    {
        // Downstream SplitApplier drops a split at or below the image height, so a row that rounds
        // up to origHeight on a short page must be clamped to the last real row.
        Assert.Equal(expected, SplitDetectionService.MapRowToOriginal(row, vscale, origHeight));
    }

    /// <summary>
    /// Writes a page NetVips can decode. The service decodes the page before it reaches the session
    /// factory, so a test that needs the operation to get past the pre-flight checks has to hand it
    /// a real image, not a few arbitrary bytes.
    /// </summary>
    private string WriteGradientImage(string fileName, int height)
    {
        // 768 wide so the detector's resize is a no-op, with each row a constant whose value encodes the
        // row index. The synthetic detector reports the row mean, so the detected confidence is a
        // read-out of which row the value came from. The offset keeps every value above the model's
        // 0.5 threshold, so *every* row in range is a peak: a row the assembly skipped shows up as a
        // missing split rather than hiding in a below-threshold value.
        const int width = 768;
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            byte value = (byte)(128 + (y % 128));
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
            }
        }

        string path = Path.Combine(_root, fileName);
        using var image = Image.NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
        return path;
    }

    private string WriteGrayscaleImage(string fileName, byte fill, int height)
    {
        // One band, written as a greyscale PNG so it reads back as B_W: the resize is still a no-op
        // (768 wide), and the band count is what the grayscale path differs in.
        const int width = 768;
        var pixels = new byte[width * height];
        Array.Fill(pixels, fill);

        string path = Path.Combine(_root, fileName);
        using var image = Image.NewFromMemory(pixels, width, height, 1, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
        return path;
    }

    private string WriteTransparentImage(string fileName, int height, int transparentFrom, int rows)
    {
        // 768 wide so the detector's resize is a no-op, 4 bands so the image really carries alpha, and
        // Interpretation.Srgb so libvips recognises the fourth band as alpha at all (a multiband image
        // with four bands does not).
        const int width = 768;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            bool transparent = y >= transparentFrom && y < transparentFrom + rows;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = 200;
                pixels[i + 1] = 200;
                pixels[i + 2] = 200;
                pixels[i + 3] = transparent ? (byte)0 : (byte)255;
            }
        }

        string path = Path.Combine(_root, fileName);
        // No interpretation is set here: the file is written and then read back by the service, and
        // libvips assigns RGBA (sRGB with an alpha band) from the PNG's own colour type, which is what
        // makes HasAlpha() true on the way in.
        using var image = Image.NewFromMemory(pixels, width, height, 4, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
        return path;
    }

    private string WriteDecodableImage(string fileName, byte fill = 128, int height = 4)
    {
        // 768 wide so the detector's fixed-width resize is a no-op, and normally short so the packed
        // tensor stays small. A taller image is what makes the resize a no-op in the other direction
        // too: at width 768 the resized height equals the source height, which is how the tall-page
        // test reaches a resized height of several thousand rows. The content is irrelevant except
        // where a test makes it matter through the model: a black page drives the row mean to 0, which
        // is how the non-finite test reaches 0/0.
        const int width = 768;
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, fill);

        string path = Path.Combine(_root, fileName);
        using var image = Image.NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
        return path;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_AfterTheModelIsReplaced_ReProbesInsteadOfReplayingTheVerdict()
    {
        // A structural failure is remembered for the process, which is what keeps a detector the
        // provider cannot run from being rebuilt for every page. Keyed on the path alone, that verdict
        // outlived the file it described: replacing a broken detector left it blacklisted until the
        // process restarted.
        string modelsDir = Path.Combine(_root, "replaced_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = Path.Combine(modelsDir, "page_break_detector.onnx");
        File.WriteAllBytes(modelPath, [1, 2, 3]); // not an ONNX model at all

        var factory = new OnnxSessionFactory(
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir, UseCPU = true }),
            NullLogger<OnnxSessionFactory>.Instance
        );
        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir, UseCPU = true })
        );

        SplitDetectionResult first = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("replaced.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        Assert.NotNull(first.Error);

        // Same path, different model: the recorded verdict must not apply to it any more.
        TinyOnnxModel.WritePeakMaskDetector(modelsDir, fileName: "page_break_detector.onnx");

        SplitDetectionResult second = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("replaced.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        Assert.Null(second.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_TransientFailure_ThrowsInsteadOfReportingAPageError()
    {
        // A device-lost or an OOM during detection is not a property of the page. Reported as a
        // per-page Error it reaches SplitProcessingService, which calls SetFailedAsync once every page
        // carries one — the chapter is failed for good. Thrown with its original type it reaches the
        // worker's IsInfrastructureFailure / ClassifyStreamingFailure path, which requeues the task
        // with the spool intact, exactly as the upscaler path does. An OOM from the session factory
        // makes the classification deterministic without needing a real device failure.
        string modelsDir = Path.Combine(_root, "transient_models");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllBytes(Path.Combine(modelsDir, "page_break_detector.onnx"), [0]);
        string imagePath = WriteDecodableImage("transient_page.png");

        var factory = Substitute.For<IOnnxSessionFactory>();
        factory
            .GetOrCreateSession(Arg.Any<string>())
            .Returns(_ => throw new OutOfMemoryException("device lost"));

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            service.DetectSplitsAsync(imagePath, null, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WrappedIoFailure_IsStillTransient()
    {
        // A retryable cause can surface wrapped (an IO error inside another exception), which is
        // why the aligned classifiers walk the chain. An outer-only check failed this page into
        // the structural catch, where the wrapped IO error was cached as a model verdict for the
        // process — the failure mode the walk exists to prevent.
        string modelsDir = Path.Combine(_root, "wrapped_io_models");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllBytes(Path.Combine(modelsDir, "page_break_detector.onnx"), [0]);
        string imagePath = WriteDecodableImage("wrapped_io_page.png");

        var factory = Substitute.For<IOnnxSessionFactory>();
        factory
            .GetOrCreateSession(Arg.Any<string>())
            .Returns(_ =>
                throw new InvalidOperationException("wrapped", new IOException("disk gone"))
            );

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DetectSplitsAsync(imagePath, null, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_SetupFaultAfterReachingTheModel_IsNotCachedAsAModelVerdict()
    {
        // An argument fault after the model is reached is this code's own setup around it, not a
        // property of the model file — the same distinction the upscaler's IsModelFault makes.
        // Recorded as a known failure, a planner or setup bug was cached under the model file's
        // identity and permanently failed detection for every later chapter, outliving whatever
        // budget or input produced it. The page carries the error; the next page re-probes.
        string modelsDir = Path.Combine(_root, "own_fault_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx"
        );

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        bool failNext = true;
        factory
            .GetOrCreateSession(Arg.Any<string>())
            .Returns(_ =>
            {
                if (failNext)
                {
                    failNext = false;
                    throw new ArgumentException("planner bug");
                }

                return session;
            });

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        string imagePath = WriteDecodableImage("own_fault_page.png");

        SplitDetectionResult first = Assert.Single(
            await service.DetectSplitsAsync(imagePath, null, TestContext.Current.CancellationToken)
        );
        Assert.NotNull(first.Error);
        Assert.NotEmpty(first.Error);

        // The verdict was not cached: the next page rebuilds nothing and succeeds.
        SplitDetectionResult second = Assert.Single(
            await service.DetectSplitsAsync(imagePath, null, TestContext.Current.CancellationToken)
        );
        Assert.Null(second.Error);
        factory.Received(0).InvalidateSession(Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_OnTheCpu_UsesTheBoundedDefaultChunkNotAVramShapedBudget()
    {
        // The budget helper's floor is shaped for GPU tile planning and can never report "no
        // budget measurable", so without this the CPU path planned a whole-page pass in host RAM
        // against a number that said nothing about it — the opposite of the bounded default the
        // code and the upgrade notes promise. The plan is observable in the debug log; the splits
        // are the same either way, because the margin (not the chunk) is what makes a window exact.
        string modelsDir = Path.Combine(_root, "cpu_budget_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx"
        );

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);
        factory.GetEffectiveBackend().Returns(GpuBackend.CPU);

        var logger = new CapturingLogger();
        var service = new SplitDetectionService(
            factory,
            logger,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteGradientImage("cpu_budget.png", 7000),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Null(result.Error);
        Assert.Contains(
            logger.Messages,
            m => m.Contains("4 window(s) of 2048 rows", StringComparison.Ordinal)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ModelWithAFixedWidthOtherThan768_ReportsAContractError()
    {
        // The detector resizes every page to 768 px wide, and the height stays dynamic. A model that
        // bakes a different fixed width is a different file than the pinned export: it would reject
        // every Run with a shape error naming neither the model nor the expectation, and — coming out
        // of the session — would be remembered as a structural failure for the process. The contract
        // check turns it into a clear, once-per-file message instead.
        string modelsDir = Path.Combine(_root, "fixed_width_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx",
            inputDims: [1, 3, -1, 1024]
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

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("fixed_width.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(result.Error);
        Assert.Contains("fixed width of 1024", result.Error);
        Assert.Contains("768", result.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ModelWithAFixedChannelCountOtherThanThree_ReportsAContractError()
    {
        // The contract check validates the input name, its element type and a fixed width, but the
        // channel dimension is just as fixed by the [1, 3, H, W] tensor the row loop builds. A model
        // that bakes C=1 passes the other checks and then fails inside Run with a shape error that
        // names neither the model nor the expectation — and, coming out of the session, that failure
        // is remembered as a structural verdict for the process. It has to be reported once, by the
        // contract check, instead.
        string modelsDir = Path.Combine(_root, "fixed_channels_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx",
            inputDims: [1, 1, -1, 768]
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

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("fixed_channels.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(result.Error);
        Assert.Contains("fixed channel count of 1", result.Error);
        Assert.Contains("3 (RGB)", result.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_ModelWithAFixedBatchOtherThanOne_ReportsAContractError()
    {
        // Same rationale as the channel and width checks: the batch dimension is fixed at 1 by the
        // tensor the row loop builds, and a model that bakes another value would otherwise fail inside
        // Run and be cached as an unusable detector for the process.
        string modelsDir = Path.Combine(_root, "fixed_batch_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx",
            inputDims: [2, 3, -1, 768]
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

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteDecodableImage("fixed_batch.png"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(result.Error);
        Assert.Contains("fixed batch size of 2", result.Error);
        Assert.Contains("one page at a time", result.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_OnMemoryPressure_HalvesTheWindowAndRecovers()
    {
        // The plan is derived from a *measured* budget, and another process can take VRAM between the
        // measurement and the run, so an over-committed window fails identically on every retry. The
        // descent halves the chunk (the margin stays) and re-runs from a clean probs/peakMask; this is
        // the recovery the production memory-pressure path relies on. Injected at the detector's Run
        // seam so it needs no GPU: any window taller than the threshold fails, the halved windows are
        // short enough, and the page still comes back detected.
        string modelsDir = Path.Combine(_root, "descent_models");
        Directory.CreateDirectory(modelsDir);
        string modelPath = TinyOnnxModel.WritePeakMaskDetector(
            modelsDir,
            fileName: "page_break_detector.onnx"
        );

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);
        // CPU forces the bounded default chunk (2048 rows over 7000), so the plan is the same on any
        // host with or without a GPU; the descent then steps 2048 -> 1024 -> 512.
        factory.GetEffectiveBackend().Returns(GpuBackend.CPU);

        const int failingWindowRows = 4000;
        int largestWindow = 0;
        // The seam is AsyncLocal, so this reaches only this test's logical call chain and never a
        // test running beside it (the same guarantee OnnxTiler.RunImplOverride relies on).
        SplitDetectionService.RunImplOverride.Value = (runSession, inputs, outputNames, options) =>
        {
            int windowRows = inputs.First().AsTensor<float>().Dimensions[2];
            largestWindow = Math.Max(largestWindow, windowRows);
            if (windowRows > failingWindowRows)
            {
                throw new OutOfMemoryException("injected device memory pressure");
            }

            return runSession.Run(inputs, outputNames, options);
        };

        var logger = new CapturingLogger();
        var service = new SplitDetectionService(
            factory,
            logger,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            Options.Create(new UpscalerConfig { ModelsDirectory = modelsDir })
        );

        SplitDetectionResult result = Assert.Single(
            await service.DetectSplitsAsync(
                WriteGradientImage("descent.png", 7000),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        // A window that would not fit was attempted (so the descent was needed)...
        Assert.True(
            largestWindow > failingWindowRows,
            $"no over-budget window was attempted; largest was {largestWindow} rows"
        );
        Assert.Contains(
            logger.Messages,
            m => m.Contains("halving the window and retrying", StringComparison.Ordinal)
        );
        // ...and the page came back fully detected from the smaller windows.
        Assert.Null(result.Error);
        Assert.NotEmpty(result.Splits);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveModelPath_DoesNotLoadATestDataFixtureUnderTheProcessCwd()
    {
        // The removed repository-relative fallback loaded <CWD>/test_data/models/page_break_detector.onnx
        // as the detector, and the startup pinned-hash verification only covers the models directory,
        // so a file planted there ran unverified. With it gone, the only places a missing detector is
        // looked for are the configured models directory and the application base directory — even
        // when the old fixture location exists and the root is the default one.
        string previousRoot = SplitDetectionLayout.Root;
        string fixtureDir = Path.Combine(Directory.GetCurrentDirectory(), "test_data", "models");
        string fixture = Path.Combine(fixtureDir, "page_break_detector.onnx");
        bool fixtureDirExisted = Directory.Exists(fixtureDir);
        bool fixtureExisted = File.Exists(fixture);
        SplitDetectionLayout.Root = AppContext.BaseDirectory;
        try
        {
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllBytes(fixture, [1, 2, 3]);

            string modelsDir = Path.Combine(_root, "empty_models");
            Directory.CreateDirectory(modelsDir);

            string resolved = SplitDetectionLayout.ResolveModelPath(modelsDir);

            Assert.NotEqual(fixture, resolved);
            Assert.Equal(Path.Combine(modelsDir, "page_break_detector.onnx"), resolved);
        }
        finally
        {
            SplitDetectionLayout.Root = previousRoot;
            if (!fixtureExisted && File.Exists(fixture))
            {
                File.Delete(fixture);
            }

            if (!fixtureDirExisted && Directory.Exists(fixtureDir))
            {
                Directory.Delete(fixtureDir, recursive: true);
            }
        }
    }

    /// <summary>An ILogger that records the formatted messages, for asserting logged decisions.</summary>
    private sealed class CapturingLogger : ILogger<SplitDetectionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task RunIsStillGoing_TracksWhetherTheAbandonedRunHasReturned()
    {
        // The RunOptions handed to the native Run may only be freed once it has returned, so the
        // timeout and cancellation paths both ask this. A run that was never started (null) is safe to
        // free, a completed task means the native call has returned, and only a still-running one has
        // to keep the handle alive — disposing it there is a use-after-free in the native call.
        Assert.False(SplitDetectionService.RunIsStillGoing(null));

        var completion = new TaskCompletionSource();
        Assert.True(SplitDetectionService.RunIsStillGoing(completion.Task));

        completion.SetResult();
        await completion.Task;
        Assert.False(SplitDetectionService.RunIsStillGoing(completion.Task));
    }
}

/// <summary>
/// Serializes tests that mutate the process-wide <see cref="SplitDetectionLayout.Root" />.
/// </summary>
[CollectionDefinition("SplitDetectionLayout", DisableParallelization = true)]
public class SplitDetectionLayoutCollection;
