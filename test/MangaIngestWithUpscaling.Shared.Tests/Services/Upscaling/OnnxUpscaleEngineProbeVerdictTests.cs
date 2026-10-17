using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NetVips;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// Pins the scope of the device-probe verdict. The probe runs through the decode, which rejects an
/// output whose geometry is at a different scale, and it can also fail for a transient reason
/// (memory, IO). Neither may disable a good model for the whole process.
/// </summary>
public class OnnxUpscaleEngineProbeVerdictTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineProbeVerdictTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_probe_verdict_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WrongScaleProbe_DoesNotDisableTheModelForItsOwnScale()
    {
        // A 4x page resolves to the 2x model through the last-resort candidate selection. Its probe
        // at 4x is rejected by the decode (the output is at a different scale), and that verdict must
        // be scoped to 4x: the same model is the only one a 2x page can use.
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale, fileName: "2x_tiny.onnx");
        string input = CreateTestJpeg(64, 90);

        using OnnxSessionFactory sessionFactory = CreateCpuSessionFactory();
        OnnxUpscaleEngine engine = CreateEngine(sessionFactory);

        string wrongScaleOutput = Path.Combine(_tempDir, "wrong-scale.webp");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UpscaleFileAsync(
                input,
                wrongScaleOutput,
                scale: 4,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );
        Assert.False(File.Exists(wrongScaleOutput));

        string output = Path.Combine(_tempDir, "right-scale.webp");
        await engine.UpscaleFileAsync(
            input,
            output,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            TestContext.Current.CancellationToken
        );

        using var outImg = Image.NewFromFile(output);
        Assert.Equal(64 * scale, outImg.Width);
        Assert.Equal(90 * scale, outImg.Height);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TransientProbeFailure_IsRetriedOnTheNextPage()
    {
        // The first probe fails with an OutOfMemoryException, which says nothing about the model. It
        // must skip the model for that attempt without caching "unusable", so a later page retries it
        // and succeeds. Because every candidate was skipped by a transient failure, the original
        // exception type has to survive instead of being flattened into "no model can upscale this"
        // (an InvalidOperationException, which upstream is treated as a permanent, spool-dropping
        // failure).
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale, fileName: "2x_tiny.onnx");
        string input = CreateTestJpeg(64, 90);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var flaky = new TransientProbeSessionFactory(
            new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance)
        );
        var engine = new OnnxUpscaleEngine(
            flaky,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string firstOutput = Path.Combine(_tempDir, "transient-first.webp");
        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            engine.UpscaleFileAsync(
                input,
                firstOutput,
                scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );

        string output = Path.Combine(_tempDir, "transient-second.webp");
        await engine.UpscaleFileAsync(
            input,
            output,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            TestContext.Current.CancellationToken
        );

        Assert.True(File.Exists(output));
        Assert.True(
            flaky.ScopeCalls >= 2,
            "the probe was not retried on the second page; the transient failure was cached"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TransientProbeFailureAfterAStructuralOne_WinsAndSurfacesItsType()
    {
        // Two candidates: the first fails structurally (a corrupt graph), the second transiently (a
        // resource hiccup). Neither succeeds, so the page fails — but the transient one is the one a
        // retry could get past, and it must be the exception that surfaces. Flattening to a generic
        // InvalidOperationException here would make a recoverable failure look permanent upstream.
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale, fileName: "2x_a_structural.onnx");
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale, fileName: "2x_b_transient.onnx");
        string input = CreateTestJpeg(64, 90);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var factory = new PerModelProbeSessionFactory(
            new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance),
            structuralModel: Path.Combine(_tempDir, "2x_a_structural.onnx"),
            transientModel: Path.Combine(_tempDir, "2x_b_transient.onnx")
        );
        var engine = new OnnxUpscaleEngine(
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string output = Path.Combine(_tempDir, "structural-then-transient.webp");
        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            engine.UpscaleFileAsync(
                input,
                output,
                scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );
    }

    private OnnxUpscaleEngine CreateEngine(OnnxSessionFactory sessionFactory)
    {
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        return new OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );
    }

    private OnnxSessionFactory CreateCpuSessionFactory()
    {
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        return new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
    }

    /// <summary>
    /// Writes a non-uniform JPEG so the decoded bytes are not all identical.
    /// </summary>
    private string CreateTestJpeg(int width, int height)
    {
        using var noise = Image
            .Gaussnoise(width, height, mean: 128, sigma: 40)
            .Cast(Enums.BandFormat.Uchar);
        using var rgb = noise.Bandjoin(noise).Bandjoin(noise);
        string path = Path.Combine(_tempDir, "input.jpg");
        rgb.WriteToFile(path);
        return path;
    }

    [Fact]
    public void IsModelFault_ClassifiesOnlyTheModelsOwnFailures()
    {
        // IsModelFault is the predicate for two things: whether the cached session should be
        // invalidated (a rebuild can help), and which failures are eligible for the process-lifetime
        // "unusable" verdict. It is not the verdict itself — that now requires a deterministic fault
        // or a repeated failure (see OnnxUpscaleEngine.RecordModelFailure), and the non-finite output
        // below is deliberately retried without any verdict at all. What this test pins is the
        // classification: the model's own failures — a mis-executing kernel, output geometry that
        // does not match the requested scale, an input the engine does not name the way it requires,
        // or the provider refusing the graph (an OnnxRuntimeException) — are model faults.
        Assert.True(OnnxUpscaleEngine.IsModelFault(new NonFiniteModelOutputException()));
        Assert.True(OnnxUpscaleEngine.IsModelFault(new ModelContractMismatchException("geometry")));

        // Not for our own configuration or programming errors. A bad TileSize throws
        // InvalidOperationException from the tile-too-large guard and a tiler invariant throws
        // ArgumentException; treating either as a model fault took a working model out of service for
        // the rest of the process and ended in a misleading "no model can upscale this page".
        Assert.False(
            OnnxUpscaleEngine.IsModelFault(new InvalidOperationException("tile too large"))
        );
        Assert.False(OnnxUpscaleEngine.IsModelFault(new ArgumentException("tile height")));
        Assert.False(OnnxUpscaleEngine.IsModelFault(new OutOfMemoryException()));
    }

    [Fact]
    public void IsCandidateModelFailure_SeparatesModelFaultsFromRetryableOnes()
    {
        // A model that passes the 256 px probe can still fail on the real grid — a shape the EP's
        // kernel does not implement, an output whose geometry does not match the scale. That has to
        // skip the candidate rather than abort the page while a working one follows.
        Assert.True(
            OnnxUpscaleEngine.IsCandidateModelFailure(new InvalidOperationException("shape"))
        );
        Assert.True(OnnxUpscaleEngine.IsCandidateModelFailure(new NonFiniteModelOutputException()));

        // A resource hiccup or an IO error says nothing about the model: it must stay retryable, so
        // the caller can retry the page instead of losing the model for the rest of the process.
        Assert.False(OnnxUpscaleEngine.IsCandidateModelFailure(new OutOfMemoryException()));
        Assert.False(OnnxUpscaleEngine.IsCandidateModelFailure(new IOException("disk")));

        // Cancellation and a disposed session factory are not the model's fault either.
        Assert.False(OnnxUpscaleEngine.IsCandidateModelFailure(new OperationCanceledException()));
        Assert.False(
            OnnxUpscaleEngine.IsCandidateModelFailure(new ObjectDisposedException("factory"))
        );
    }

    /// <summary>
    /// Delegates to a real factory, but makes the first <see cref="EnterInferenceScope"/> call fail
    /// with an <see cref="OutOfMemoryException"/> — the probe's failure mode on a transient device
    /// hiccup, without needing to exhaust a device.
    /// </summary>
    private sealed class TransientProbeSessionFactory(IOnnxSessionFactory inner)
        : IOnnxSessionFactory
    {
        private int _scopeCalls;

        public int ScopeCalls => Volatile.Read(ref _scopeCalls);

        public InferenceSession GetOrCreateSession(string modelPath) =>
            inner.GetOrCreateSession(modelPath);

        public InferenceSession CreateSession(string modelPath) => inner.CreateSession(modelPath);

        public void InvalidateSession(string modelPath) => inner.InvalidateSession(modelPath);

        public void InvalidateAllSessions() => inner.InvalidateAllSessions();

        public GpuBackend GetEffectiveBackend() => inner.GetEffectiveBackend();

        public IDisposable EnterInferenceScope()
        {
            // #1 is the first page's probe; it fails. #2 is the retried probe on the second page.
            if (Interlocked.Increment(ref _scopeCalls) == 1)
            {
                throw new OutOfMemoryException("simulated transient device memory pressure");
            }

            return inner.EnterInferenceScope();
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Delegates to a real factory but fails the probe of two specific models: one structurally (a
    /// generic error, as a corrupt graph would) and one transiently (an <see cref="OutOfMemoryException"/>).
    /// Exists to pin that the transient failure is what surfaces when no candidate can be probed,
    /// even though the structural failure was seen first.
    /// </summary>
    private sealed class PerModelProbeSessionFactory(
        IOnnxSessionFactory inner,
        string structuralModel,
        string transientModel
    ) : IOnnxSessionFactory
    {
        public InferenceSession GetOrCreateSession(string modelPath)
        {
            if (string.Equals(modelPath, structuralModel, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("simulated structurally broken model");
            }

            if (string.Equals(modelPath, transientModel, StringComparison.OrdinalIgnoreCase))
            {
                throw new OutOfMemoryException("simulated transient device memory pressure");
            }

            return inner.GetOrCreateSession(modelPath);
        }

        public InferenceSession CreateSession(string modelPath) => inner.CreateSession(modelPath);

        public void InvalidateSession(string modelPath) => inner.InvalidateSession(modelPath);

        public void InvalidateAllSessions() => inner.InvalidateAllSessions();

        public GpuBackend GetEffectiveBackend() => inner.GetEffectiveBackend();

        public IDisposable EnterInferenceScope() => inner.EnterInferenceScope();

        public void Dispose() => inner.Dispose();
    }
}
