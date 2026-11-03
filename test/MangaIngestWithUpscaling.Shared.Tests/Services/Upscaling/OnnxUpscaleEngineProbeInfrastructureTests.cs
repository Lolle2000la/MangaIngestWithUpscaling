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
/// The device probe treats a failure as the model's only when it really is the model's. An
/// <see cref="ObjectDisposedException"/> from the singleton session factory means shutdown raced the
/// page, not that the model is unusable, so it has to propagate as the infrastructure failure it is
/// rather than being swallowed into a structural "skip this candidate" verdict. Swallowing it
/// disabled every candidate and ended in the permanent, generic "no model can upscale" error, which
/// upstream drops the chapter's spool. The classification mirrors
/// <see cref="OnnxUpscaleEngine.IsCandidateModelFailure"/>, which excludes the type on the real-tile
/// path for the same reason.
/// </summary>
public class OnnxUpscaleEngineProbeInfrastructureTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineProbeInfrastructureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_probe_infra_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProbeTimeObjectDisposedException_SurfacesInsteadOfTheGenericNoModelFailure()
    {
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale, fileName: "2x_tiny.onnx");
        string input = CreateTestJpeg(64, 90);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var factory = new DisposedOnScopeFactory(
            new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance)
        );
        var engine = new OnnxUpscaleEngine(
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string output = Path.Combine(_tempDir, "disposed.webp");
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            engine.UpscaleFileAsync(
                input,
                output,
                scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );

        // The spool-dropping outcome is the generic permanent error this test exists to rule out.
        Assert.False(File.Exists(output));
    }

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

    /// <summary>
    /// Delegates to a real factory but makes every <see cref="EnterInferenceScope"/> throw the way a
    /// factory being disposed on another thread does, so the probe's handling of a shutdown race can
    /// be driven without racing a real shutdown.
    /// </summary>
    private sealed class DisposedOnScopeFactory(IOnnxSessionFactory inner) : IOnnxSessionFactory
    {
        public InferenceSession GetOrCreateSession(string modelPath) =>
            inner.GetOrCreateSession(modelPath);

        public InferenceSession CreateSession(string modelPath) => inner.CreateSession(modelPath);

        public void InvalidateSession(string modelPath) => inner.InvalidateSession(modelPath);

        public void InvalidateAllSessions() => inner.InvalidateAllSessions();

        public GpuBackend GetEffectiveBackend() => inner.GetEffectiveBackend();

        public IDisposable EnterInferenceScope() => throw new ObjectDisposedException("factory");

        public void Dispose() => inner.Dispose();
    }
}
