using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NetVips;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// The automatic VRAM budget discounts the engine's own cached session.
/// <para>
/// ONNX Runtime's arenas keep their high-water mark, so after a page the accelerator still shows that
/// page's peak as used. Without the discount every page after the first was planned against a card the
/// previous page had filled: measured on an RX 9070 XT, 1125x1600 page, fp16 4x ESRGAN, 10.8 GB
/// budget, page 1 planned 2x2 of 563x800 (512 px context) and page 2 planned 5x7 of 225x229 from the
/// 1.3 GB residual — below the 256 px quality floor — with the session rebuilt on every page because
/// the shape kept changing. The discount is only taken while the factory really caches that session:
/// the page-break detector shares the factory and can evict it between two pages.
/// </para>
/// </summary>
public class OnnxUpscaleEngineVramBudgetTests : IDisposable
{
    private const int Width = 256;
    private const int Height = 288;
    private const int Scale = 2;

    /// <summary>
    /// A fixed budget for the recording engine below. Large enough for the tiny model that the whole
    /// page is a single tile, so the footprint under test is that one grid's.
    /// </summary>
    private const long FixedBudget = 4L * 1024 * 1024 * 1024;

    private readonly string _tempDir;

    public OnnxUpscaleEngineVramBudgetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_engine_budget_{Guid.NewGuid():N}");
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
    public async Task AutomaticBudget_DiscountsTheCachedSessionFromTheSecondPageOn()
    {
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var factory = new CachedSessionFactory(modelPath);
        var engine = new RecordingBudgetEngine(
            factory,
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );

        await RunPageAsync(engine, "1.webp");
        await RunPageAsync(engine, "2.webp");
        await RunPageAsync(engine, "3.webp");

        // One budget read per page. The first page holds no session, so there is nothing to discount;
        // from the second page on the discount is the grid the first page ran — and it stays the same
        // figure, because the plan stays the same, which is the point: a discount that let the plan
        // shrink would feed back into a smaller discount and walk the grid down page by page.
        Assert.Equal(3, engine.OwnSessionBytesSeen.Count);
        Assert.Equal(0, engine.OwnSessionBytesSeen[0]);
        Assert.Equal(ExpectedFootprint(modelPath), engine.OwnSessionBytesSeen[1]);
        Assert.Equal(ExpectedFootprint(modelPath), engine.OwnSessionBytesSeen[2]);
        Assert.True(engine.OwnSessionBytesSeen[1] > 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AutomaticBudget_DiscountsWhatTheDeviceChargedThisProcess()
    {
        // The discount is measured, not only predicted: ONNX Runtime's arena outlives a released
        // session, so after the GTT safety net flushed the cache the device still reports the page's
        // memory. The measured delta covers that, a benchmark's leftover session and a retry's arena —
        // none of which the predicted peak knows about. Here the device charges more than the
        // prediction, so the larger figure is discounted.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var engine = new RecordingBudgetEngine(
            new CachedSessionFactory(modelPath),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );
        engine.DeviceReadings.Enqueue((1000L * 1024 * 1024, 10L));
        engine.DeviceReadings.Enqueue((9000L * 1024 * 1024, 10L)); // page 1: +8000 MiB
        engine.DeviceReadings.Enqueue((9000L * 1024 * 1024, 10L));
        engine.DeviceReadings.Enqueue((9000L * 1024 * 1024, 10L));

        await RunPageAsync(engine, "1.webp");
        await RunPageAsync(engine, "2.webp");

        // Page 2 measured nothing of its own (it reused the session), so it keeps page 1's figure:
        // that persistence is what makes the budget stable instead of wobbling between pages.
        Assert.Equal(new long[] { 0, 8_000L * 1024 * 1024 }, engine.OwnSessionBytesSeen);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AutomaticBudget_PrefersTheDriversPerProcessFigureForTheDiscount()
    {
        // The discount is kept at its largest across the process, so a single page on which another
        // tenant allocated would inflate the budget for every page after it. The driver's per-process
        // figure is what makes that impossible; the system-wide one is only the fallback.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var engine = new RecordingBudgetEngine(
            new CachedSessionFactory(modelPath),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );
        engine.DeviceReadings.Enqueue((1000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((9000L * 1024 * 1024, 0L)); // system-wide: +8000
        engine.DeviceReadings.Enqueue((9000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((9000L * 1024 * 1024, 0L));
        // Four per-process reads on the page that builds its shape; ours grows by 8500 across the
        // page while the system-wide figure grows by 8000. The first read is the one the budget
        // discounts, so the first page already discounts the 500 MiB the process holds (its context
        // and the model's weights) instead of nothing.
        engine.ProcessReadings.Enqueue(500L * 1024 * 1024); // page start, discounted
        engine.ProcessReadings.Enqueue(500L * 1024 * 1024); // plan line
        engine.ProcessReadings.Enqueue(500L * 1024 * 1024); // the recording (nothing of its own)
        engine.ProcessReadings.Enqueue(9000L * 1024 * 1024); // after the page

        await RunPageAsync(engine, "1.webp");
        await RunPageAsync(engine, "2.webp");

        Assert.Equal(
            new long[] { 500L * 1024 * 1024, 8_500L * 1024 * 1024 },
            engine.OwnSessionBytesSeen
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AutomaticBudget_DiscountsWhatThisProcessHoldsNotWhatTheGridCosts()
    {
        // The prediction describes one tile grid; the process holds the arena of whatever ran last.
        // Measured on an RTX 5070 Ti (1125x1600, fp16 4x ESRGAN, 2x2 of 563x800) the grid's cost
        // stayed at 7416 MB while the process grew from 7712 MB to 8738 MB over 24 pages, so the
        // budget fell by 1 GB over one chapter with nothing else on the card changing. The discount
        // has to follow the process, or every page of growth is charged to the other tenants.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var engine = new RecordingBudgetEngine(
            new CachedSessionFactory(modelPath),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );
        // Page 1 builds the shape and reads four times (start, plan, recording, after); a page that
        // reuses the shape reads three (start, plan, after). The first read of a page is the one the
        // budget discounts, so the discount is the process's figure and follows its growth.
        engine.ProcessReadings.Enqueue(300L * 1024 * 1024); // page 1 start, discounted
        engine.ProcessReadings.Enqueue(300L * 1024 * 1024); // page 1 plan
        engine.ProcessReadings.Enqueue(300L * 1024 * 1024); // page 1 recording
        engine.ProcessReadings.Enqueue(6000L * 1024 * 1024); // page 1 after
        engine.ProcessReadings.Enqueue(6000L * 1024 * 1024); // page 2 start, discounted
        engine.ProcessReadings.Enqueue(6000L * 1024 * 1024); // page 2 plan
        engine.ProcessReadings.Enqueue(6000L * 1024 * 1024); // page 2 after
        engine.ProcessReadings.Enqueue(9500L * 1024 * 1024); // page 3 start, discounted
        engine.ProcessReadings.Enqueue(9500L * 1024 * 1024); // page 3 plan
        engine.ProcessReadings.Enqueue(9500L * 1024 * 1024); // page 3 after

        await RunPageAsync(engine, "1.webp");
        await RunPageAsync(engine, "2.webp");
        await RunPageAsync(engine, "3.webp");

        Assert.Equal(
            new long[] { 300L * 1024 * 1024, 6000L * 1024 * 1024, 9500L * 1024 * 1024 },
            engine.OwnSessionBytesSeen
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AutomaticBudget_IsReadAfterTheFirstUseBenchmark()
    {
        // The benchmark and the model probe allocate device memory of their own and ONNX Runtime's
        // arenas keep their high-water mark, so that memory is resident while the page runs. A budget
        // read before them plans the page as if it were free: on an RX 9070 XT that planned 2x2 of
        // 563x800 (predicted 10.5 GB) for a page that then peaked at 15.2 GB of the 16.3 GB card.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var factory = new CachedSessionFactory(modelPath);
        var order = new List<string>();
        var calibrator = Substitute.For<IDeviceMemoryCalibrator>();
        calibrator
            .GetOrCalibrateForModelAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                lock (order)
                {
                    order.Add("benchmark");
                }

                return Task.FromResult<DeviceMemoryProfile?>(null);
            });
        var engine = new RecordingBudgetEngine(
            factory,
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            calibrator,
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget,
            onBudgetRead: () =>
            {
                lock (order)
                {
                    order.Add("budget");
                }
            }
        );

        await RunPageAsync(engine, "1.webp");

        Assert.Equal(new[] { "benchmark", "budget" }, order);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplicitBudgetBytes_IsNeverDiscounted()
    {
        // An explicit MemoryBudgetBytes is the operator's own number for the whole session, so it is
        // taken as written rather than adjusted by what the process happens to hold.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var factory = new CachedSessionFactory(modelPath);
        var engine = new RecordingBudgetEngine(
            factory,
            Options.Create(
                new UpscalerConfig
                {
                    ModelsDirectory = _tempDir,
                    UseCPU = true,
                    MemoryBudgetBytes = "4GiB",
                }
            ),
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );

        await RunPageAsync(engine, "1.webp");
        await RunPageAsync(engine, "2.webp");

        Assert.Empty(engine.OwnSessionBytesSeen);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Page_PrefersTheDriversPerProcessFigureForTheFootprintItRecords()
    {
        // The system-wide figure counts every other tenant, so another process allocating or freeing
        // while the page runs lands in the measurement — the same padded shape was measured at 8406 MB
        // once and 9683 MB another time, and the lower one reached a plan. The driver's per-process
        // figure cannot do that, so it is the source whenever it exists.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var calibrator = Substitute.For<IDeviceMemoryCalibrator>();
        var engine = new RecordingBudgetEngine(
            new CachedSessionFactory(modelPath),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            calibrator,
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );
        // System-wide: 1000 -> 7000, a 6000 MB delta...
        engine.DeviceReadings.Enqueue((1000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((1000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7000L * 1024 * 1024, 0L));
        // ...and the driver's own figure says the page cost this process 9000 MB, which is what the
        // next plan has to be sized from.
        // Four per-process reads per page: around the page and at the plan line, plus the recording's.
        engine.ProcessReadings.Enqueue(500L * 1024 * 1024); // page start
        engine.ProcessReadings.Enqueue(500L * 1024 * 1024); // plan line
        engine.ProcessReadings.Enqueue(9500L * 1024 * 1024); // the recording
        engine.ProcessReadings.Enqueue(9500L * 1024 * 1024); // after the page

        await RunPageAsync(engine, "1.webp");

        OnnxTiler.TileSplit split = PlannedSplit(modelPath);
        (int paddedWidth, int paddedHeight) = OnnxTiler.UniformTileTensorShape(split);
        calibrator
            .Received(1)
            .RecordObservedTilePeak(
                0,
                _tempDir,
                modelPath,
                Scale,
                paddedWidth,
                paddedHeight,
                9000L * 1024 * 1024
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Page_RecordsTheFootprintItMeasuredForItsTileShape()
    {
        // The per-model calibration fits its slope at two small benchmark tiles (96/192 px) and
        // extrapolates it to the operating shape, so a provider whose overhead does not scale exactly
        // quadratically is estimated rather than measured. The page is the only place the real figure
        // can be taken, and the next plan for that shape has to be sized from it.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, Scale);
        var calibrator = Substitute.For<IDeviceMemoryCalibrator>();
        var engine = new RecordingBudgetEngine(
            new CachedSessionFactory(modelPath),
            Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }),
            calibrator,
            NullLogger<OnnxUpscaleEngine>.Instance,
            FixedBudget
        );
        // Per page: the outer read before the page, the plan-line read, the read the recording takes,
        // and the outer read after the page.
        engine.DeviceReadings.Enqueue((1000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((1000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7000L * 1024 * 1024, 0L));
        // The second page runs the same shape, which the session already holds: it allocates nothing
        // of its own, so it measures nothing and must not record. Its readings are deliberately *not*
        // flat — a page at a reused shape still shows growth from whatever else allocated while it
        // ran — because that is exactly the figure the shape guard exists to keep out of the store.
        engine.DeviceReadings.Enqueue((7000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7000L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7500L * 1024 * 1024, 0L));
        engine.DeviceReadings.Enqueue((7500L * 1024 * 1024, 0L));

        await RunPageAsync(engine, "1.webp");
        await RunPageAsync(engine, "2.webp");

        OnnxTiler.TileSplit split = PlannedSplit(modelPath);
        (int paddedWidth, int paddedHeight) = OnnxTiler.UniformTileTensorShape(split);
        // Exactly one call, not "one call with this figure": the reused page would record a different
        // figure, and an argument-matching Received(1) would not notice it.
        calibrator
            .Received(1)
            .RecordObservedTilePeak(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<long>()
            );
        calibrator
            .Received(1)
            .RecordObservedTilePeak(
                0,
                _tempDir,
                modelPath,
                Scale,
                paddedWidth,
                paddedHeight,
                6000L * 1024 * 1024
            );
    }

    /// <summary>
    /// The footprint the engine should discount after a page: the planner's predicted peak for the grid
    /// it ran. Recomputed here from the same inputs so the test pins the figure rather than "something
    /// positive" — a discount built from the wrong grid would size the next page against the wrong
    /// memory.
    /// </summary>
    private static long ExpectedFootprint(string modelPath)
    {
        long modelSizeBytes = new FileInfo(modelPath).Length;
        OnnxTiler.TileSplit split = PlannedSplit(modelPath);

        return OnnxTiler.PredictTilePeakBytes(
            split,
            Scale,
            modelPath,
            modelSizeBytes,
            IsFp16(modelPath)
        );
    }

    private static OnnxTiler.TileSplit PlannedSplit(string modelPath) =>
        OnnxTiler.PlanTileSplit(
            Width,
            Height,
            Scale,
            modelPath,
            FixedBudget,
            new FileInfo(modelPath).Length,
            IsFp16(modelPath)
        );

    private static bool IsFp16(string modelPath)
    {
        using var probe = new InferenceSession(modelPath);
        return probe.InputMetadata.TryGetValue("input", out var meta)
            && meta.ElementType == typeof(Float16);
    }

    private async Task RunPageAsync(OnnxUpscaleEngine engine, string outputName)
    {
        await engine.UpscaleFileAsync(
            CreateTestJpeg(),
            Path.Combine(_tempDir, outputName),
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
        string path = Path.Combine(_tempDir, $"engine_budget_{Guid.NewGuid():N}.jpg");
        image.WriteToFile(path, new VOption());
        return path;
    }

    /// <summary>
    /// An engine that records the footprint it was asked to discount and returns a fixed budget, so the
    /// discount can be asserted without a GPU and without the device's own figures.
    /// </summary>
    private sealed class RecordingBudgetEngine(
        IOnnxSessionFactory sessionFactory,
        IOptions<UpscalerConfig> config,
        IDeviceMemoryCalibrator calibrator,
        ILogger<OnnxUpscaleEngine> logger,
        long budget,
        Action? onBudgetRead = null
    ) : OnnxUpscaleEngine(sessionFactory, config, calibrator, logger)
    {
        private readonly List<long> _seen = [];

        public IReadOnlyList<long> OwnSessionBytesSeen
        {
            get
            {
                lock (_seen)
                {
                    return _seen.ToList();
                }
            }
        }

        /// <summary>Device readings handed out in order, one pair per read.</summary>
        public Queue<(long Vram, long Gtt)> DeviceReadings { get; } = new();

        /// <summary>
        /// Per-process device readings handed out in order, one per read. An empty queue is "the driver
        /// reports no per-process figure", which is the case the system-wide fallback exists for.
        /// </summary>
        public Queue<long> ProcessReadings { get; } = new();

        protected internal override long? ReadProcessMemoryBytes(int deviceId)
        {
            lock (ProcessReadings)
            {
                return ProcessReadings.Count > 0 ? ProcessReadings.Dequeue() : null;
            }
        }

        protected internal override (long VramUsed, long GttUsed) ReadDeviceMemoryUsage(
            int deviceId
        )
        {
            lock (DeviceReadings)
            {
                return DeviceReadings.Count > 0 ? DeviceReadings.Dequeue() : (0, 0);
            }
        }

        protected internal override long ReadAutoVramBudget(int deviceId, long ownSessionBytes)
        {
            lock (_seen)
            {
                _seen.Add(ownSessionBytes);
            }

            onBudgetRead?.Invoke();
            return budget;
        }
    }

    /// <summary>
    /// Hands out a real session for the tiny model.
    /// </summary>
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
