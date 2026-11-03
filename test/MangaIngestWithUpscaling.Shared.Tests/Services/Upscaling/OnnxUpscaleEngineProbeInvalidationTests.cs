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
/// The probe invalidates the cached session only for a failure that is the model's own. A tiler
/// configuration fault (a TileSize the tiler rejects, an invalid blend region) is not something a
/// rebuild changes, so invalidating for it tore down a healthy ~300 ms session once per candidate per
/// page until the config was fixed. The real-tile path already guards this with
/// <see cref="OnnxUpscaleEngine.IsModelFault"/>; the probe has to agree.
/// </summary>
public class OnnxUpscaleEngineProbeInvalidationTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineProbeInvalidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_probe_invalidation_{Guid.NewGuid():N}");
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
    public async Task ProbeTilerConfigFault_DoesNotInvalidateTheCachedSession()
    {
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale, fileName: "2x_tiny.onnx");
        string input = CreateTestJpeg(64, 90);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var factory = new ConfigFaultProbeSessionFactory(
            new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance)
        );
        var engine = new OnnxUpscaleEngine(
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string output = Path.Combine(_tempDir, "config-fault.webp");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UpscaleFileAsync(
                input,
                output,
                scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );

        // The candidate was skipped, so the page still fails with the generic selection error — but
        // the cached session must not have been rebuilt for a fault a rebuild cannot fix.
        Assert.Contains("No model in", ex.Message);
        Assert.Equal(0, factory.InvalidationCount);
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
    /// Fails every probe with the kind of error the tiler's own configuration guard produces — an
    /// <see cref="InvalidOperationException"/>, which <see cref="OnnxUpscaleEngine.IsModelFault"/>
    /// deliberately classifies as not the model's — and records how often the engine invalidates the
    /// session.
    /// </summary>
    private sealed class ConfigFaultProbeSessionFactory(IOnnxSessionFactory inner)
        : IOnnxSessionFactory
    {
        private int _invalidations;

        public int InvalidationCount => Volatile.Read(ref _invalidations);

        public InferenceSession GetOrCreateSession(string modelPath) =>
            throw new InvalidOperationException(
                "simulated tiler configuration fault (tile too large)"
            );

        public InferenceSession CreateSession(string modelPath) => inner.CreateSession(modelPath);

        public void InvalidateSession(string modelPath) =>
            Interlocked.Increment(ref _invalidations);

        public void InvalidateAllSessions() => inner.InvalidateAllSessions();

        public GpuBackend GetEffectiveBackend() => inner.GetEffectiveBackend();

        public IDisposable EnterInferenceScope() => inner.EnterInferenceScope();

        public void Dispose() => inner.Dispose();
    }
}
