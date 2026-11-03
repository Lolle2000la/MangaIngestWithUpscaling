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
/// The engine reads a model's probe verdict for this device before running the probe inference, and
/// records what it learned. That inference costs device memory the WebGPU EP never returns (measured
/// at ~4.9 GB resident for the rest of the process on an RX 9070 XT), so only the first process on a
/// device should pay it.
/// </summary>
public class OnnxUpscaleEngineProbeCacheTests : IDisposable
{
    private const int Width = 256;
    private const int Height = 288;
    private const int Scale = 2;

    private readonly string _tempDir;

    public OnnxUpscaleEngineProbeCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_probe_cache_{Guid.NewGuid():N}");
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
    [Trait("Category", "Unit")]
    public async Task RememberedVerdict_SkipsTheProbeAndRecordsNothing()
    {
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var calibrator = Substitute.For<IDeviceMemoryCalibrator>();
        calibrator
            .TryGetModelUsableVerdict(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>()
            )
            .Returns(true);

        await RunPageAsync(modelPath, calibrator);

        // The verdict was already known, so nothing was probed and nothing was written back: the early
        // return is the whole point of the cache.
        calibrator
            .DidNotReceiveWithAnyArgs()
            .SetModelUsableVerdict(default, default!, default!, default, default);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnknownVerdict_RunsTheProbeAndRecordsWhatItLearned()
    {
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var calibrator = Substitute.For<IDeviceMemoryCalibrator>();
        calibrator
            .TryGetModelUsableVerdict(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>()
            )
            .Returns((bool?)null);

        await RunPageAsync(modelPath, calibrator);

        calibrator
            .Received(1)
            .SetModelUsableVerdict(Arg.Any<int>(), _tempDir, modelPath, Scale, true);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RememberedUnusableVerdict_IsHonoured()
    {
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var calibrator = Substitute.For<IDeviceMemoryCalibrator>();
        calibrator
            .TryGetModelUsableVerdict(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>()
            )
            .Returns(false);

        var factory = new CachedSessionFactory(modelPath);
        var engine = new OnnxUpscaleEngine(
            factory,
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            calibrator,
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        // The only candidate is remembered as unusable, so the page has to fail rather than silently
        // run a model this device was already told it cannot run.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            engine.UpscaleFileAsync(
                CreateTestJpeg(),
                Path.Combine(_tempDir, "unusable.webp"),
                Scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );
    }

    private async Task RunPageAsync(string modelPath, IDeviceMemoryCalibrator calibrator)
    {
        var engine = new OnnxUpscaleEngine(
            new CachedSessionFactory(modelPath),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            calibrator,
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        await engine.UpscaleFileAsync(
            CreateTestJpeg(),
            Path.Combine(_tempDir, "out.webp"),
            Scale,
            CompressionFormat.Webp,
            quality: 80,
            TestContext.Current.CancellationToken
        );
    }

    private string CreateTestJpeg()
    {
        byte[] rgb = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 3;
                rgb[i] = (byte)(x % 256);
                rgb[i + 1] = (byte)(y % 256);
                rgb[i + 2] = (byte)((x + y) % 256);
            }
        }

        using var image = Image.NewFromMemory(rgb, Width, Height, 3, Enums.BandFormat.Uchar);
        string path = Path.Combine(_tempDir, $"probe_cache_{Guid.NewGuid():N}.jpg");
        image.WriteToFile(path, new VOption());
        return path;
    }

    /// <summary>Hands out a real session for the tiny model, as the engine's probe needs one.</summary>
    private sealed class CachedSessionFactory(string modelPath) : IOnnxSessionFactory
    {
        private readonly Lock _lock = new();
        private InferenceSession? _session;

        public InferenceSession GetOrCreateSession(string modelPath)
        {
            lock (_lock)
            {
                return _session ??= new InferenceSession(modelPath);
            }
        }

        public InferenceSession CreateSession(string modelPath) => new(modelPath);

        public void InvalidateSession(string modelPath)
        {
            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
            }
        }

        public void InvalidateAllSessions() => InvalidateSession(modelPath);

        public void ReleaseCachedSessions() => InvalidateAllSessions();

        public GpuBackend GetEffectiveBackend() => GpuBackend.CPU;

        public IDisposable EnterInferenceScope() => NoOpScope.Instance;

        public void Dispose() => InvalidateAllSessions();

        private sealed class NoOpScope : IDisposable
        {
            public static readonly NoOpScope Instance = new();

            public void Dispose() { }
        }
    }
}
