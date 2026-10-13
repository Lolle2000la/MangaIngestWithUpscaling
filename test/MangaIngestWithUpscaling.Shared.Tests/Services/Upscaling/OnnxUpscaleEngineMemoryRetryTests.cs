using System.Text.RegularExpressions;
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

public class OnnxUpscaleEngineMemoryRetryTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineMemoryRetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_engine_retry_{Guid.NewGuid():N}");
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
    public async Task MemoryPressureRetry_UsesASessionFetchedAfterTheInvalidation()
    {
        // The memory-pressure path invalidates the cached session to give the halved tile a clean
        // device, and the real factory disposes the instance it removed. The session the loop is
        // holding is that instance, so the next attempt has to fetch a new one: InferenceSession has
        // no disposed guard, and a Run on the released handle throws a native error that is not a
        // memory exception — it lands in the generic catch and loses the page instead of recovering.
        // Reusing it is what made the whole recovery path dead.
        const int width = 256;
        const int height = 288;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(width, height);

        var factory = new MemoryPressureSessionFactory(modelPath);
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        var engine = new OnnxUpscaleEngine(
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string outputPath = Path.Combine(_tempDir, "upscaled.webp");
        await engine.UpscaleFileAsync(
            inputPath,
            outputPath,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            TestContext.Current.CancellationToken
        );

        // It recovered rather than failing: the halved tile produced the whole page.
        Assert.True(File.Exists(outputPath));
        using var outImg = Image.NewFromFile(outputPath);
        Assert.Equal(width * scale, outImg.Width);
        Assert.Equal(height * scale, outImg.Height);

        // The retry asked the factory for a session after invalidating the one it held. Without the
        // re-fetch this is zero, and the attempt above runs on a disposed session.
        Assert.True(
            factory.SessionsRequestedAfterInvalidation > 0,
            "the retry did not fetch a new session after the invalidation"
        );
        // Two flushes: ReleaseSessionForShapeChange drops the probe's 64x64 session before the page's
        // own shape is run (an unknown _sessionShapeModel counts as a change), and the memory-pressure
        // retry flushes again. Without the second the OOM-causing session would stay resident through
        // the whole retry — the background drain waits for the retry's own scope to end — doubling peak
        // VRAM in the path meant to reduce it.
        Assert.Equal(2, factory.ReleaseCachedSessionsCalls);
        Assert.True(
            factory.ReleasedALiveSession,
            "the synchronous flush did not free the live session before the retry"
        );
        // The probe's scope, the scope the input metadata is read under, the scope that threw, and the
        // one the retry succeeded under. Pinned exactly so that a change in how often the probe scopes
        // breaks this test loudly — the metadata read used to run outside any scope, where a concurrent
        // eviction could dispose the session under it.
        Assert.Equal(4, factory.InferenceScopeCalls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MemoryPressureThatPersistsToTheFloor_FailsThePageInsteadOfShrinkingFurther()
    {
        // A device that cannot run the page at any tile size worth running has to fail it rather than
        // descend into postage stamps: below EmergencyTileFloor the model has no receptive-field
        // context left, so a smaller tile saves nothing but the failure. It is surfaced as the memory
        // exception it is, which the worker's classifier treats as retryable, so the chapter is
        // requeued rather than silently produced at a terrible quality.
        const int width = 256;
        const int height = 288;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(width, height);

        var factory = new MemoryPressureSessionFactory(modelPath, alwaysFails: true);
        var logger = new RecordingLogger();
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        var engine = new OnnxUpscaleEngine(
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            logger
        );

        string outputPath = Path.Combine(_tempDir, "upscaled.webp");
        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            engine.UpscaleFileAsync(
                inputPath,
                outputPath,
                scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );

        Assert.False(File.Exists(outputPath), "a failed page must not be published");

        // Every tile the descent actually attempted is at or above the floor. Parsed out of the halving
        // warnings, because that is where the engine reports the grid it is about to shrink: a descent
        // that went lower logs a smaller one here and fails this, whatever the floor constant happens to
        // say. The plan's own attempt is the first of them.
        List<int> attemptedSides = logger
            .Warnings.Where(w =>
                w.Contains("Memory pressure encountered", StringComparison.Ordinal)
            )
            .Select(w => Regex.Match(w, @"tiles of (\d+)x(\d+)"))
            .Where(m => m.Success)
            .Select(m => Math.Min(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
            .ToList();

        Assert.NotEmpty(attemptedSides);
        Assert.All(
            attemptedSides,
            side =>
                Assert.True(
                    side >= OnnxTiler.EmergencyTileFloor,
                    $"the descent attempted a {side} px tile, below the {OnnxTiler.EmergencyTileFloor} px floor: {string.Join(", ", attemptedSides)}"
                )
        );

        // The floor that stopped the descent names itself in the log, which is what makes the outcome
        // diagnosable rather than only "memory pressure".
        Assert.Contains(
            logger.Warnings,
            w => w.Contains($"{OnnxTiler.EmergencyTileFloor}px floor", StringComparison.Ordinal)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MemoryPressureAtTheTilerCall_DescendsAndRecoversRatherThanLosingThePage()
    {
        // The other retry tests inject at the scope acquisition, which sits *before* the tiler call.
        // This one injects at the engine-to-tiler call itself, through the seam the engine exposes
        // for it: that is the boundary the removed tiler-internal halve loop used to hide the
        // failure behind — an OOM from the tiler call, not from the scope, still has to reach the
        // descent, flush the session whose arena produced it, halve the grid and recover on the
        // refetched session. A wrapper that stops this call's exception from reaching the descent
        // makes this fail on the missing recovery.
        const int width = 256;
        const int height = 288;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(width, height);

        var factory = new MemoryPressureSessionFactory(modelPath, scopeFailures: false);
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        var engine = new TilerFaultEngine(
            new OutOfMemoryException("simulated pressure at the tiler call"),
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        )
        {
            FailuresLeft = 1,
        };

        string outputPath = Path.Combine(_tempDir, "upscaled_seam.webp");
        await engine.UpscaleFileAsync(
            inputPath,
            outputPath,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            TestContext.Current.CancellationToken
        );

        // It recovered rather than losing the page, and it recovered through the descent: the one
        // thrown call is followed by the flush that frees the OOM-causing session, a refetch and a
        // halved attempt — the same pins the scope-injected test asserts, reached from the seam.
        Assert.True(File.Exists(outputPath));
        using var outImg = Image.NewFromFile(outputPath);
        Assert.Equal(width * scale, outImg.Width);
        Assert.Equal(height * scale, outImg.Height);
        Assert.Equal(1, engine.Thrown);
        Assert.Equal(2, factory.ReleaseCachedSessionsCalls);
        Assert.True(
            factory.SessionsRequestedAfterInvalidation > 0,
            "the retry did not fetch a new session after the flush"
        );
        Assert.Equal(4, factory.InferenceScopeCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfigFaultAtTheTilerCall_KeepsTheHealthySessionCached()
    {
        // A fault that is this code's own configuration — the TileBlender's argument error, a
        // manual TileSize the blend region rejects — is not a model fault, by IsModelFault's own
        // contract, so it must not tear down a healthy cached session either: that costs a ~300 ms
        // rebuild on every page until the config is fixed, for a session the config error never
        // touched. The fault is injected at the tiler-call seam and has to leave the session
        // untouched on its way out.
        const int width = 256;
        const int height = 288;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(width, height);

        var factory = Substitute.For<IOnnxSessionFactory>();
        using var session = new InferenceSession(modelPath);
        factory.GetOrCreateSession(Arg.Any<string>()).Returns(session);
        factory.GetEffectiveBackend().Returns(GpuBackend.CPU);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        var engine = new TilerFaultEngine(
            new InvalidOperationException("blend region wider than the tile"),
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        )
        {
            FailuresLeft = 1,
        };

        string outputPath = Path.Combine(_tempDir, "config_fault.webp");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UpscaleFileAsync(
                inputPath,
                outputPath,
                scale,
                CompressionFormat.Webp,
                quality: 80,
                TestContext.Current.CancellationToken
            )
        );

        Assert.False(File.Exists(outputPath), "a failed page must not be published");
        factory.Received(0).InvalidateSession(Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpscaleRgb_PropagatesARunFailureInsteadOfRecoveringInternally()
    {
        // The engine owns the recovery from a failed Run (halving plus releasing the session whose
        // arena failed the allocation), so the tiler has to hand the failure out. The internal
        // halve loop this migration removed swallowed exactly this failure, and a test that
        // injects at the engine's scope acquisition cannot see a reintroduction of it — this
        // injects at the tiler's own Run seam, on the first tile of a multi-tile page, which also
        // exercises the pipeline's unwind path (a background prefetch to observe, no decode task
        // yet to drain). The override is AsyncLocal, so it reaches only this test's own call chain
        // and never the tests running beside it — a static field version of this seam failed a
        // concurrently running upscaler test with this injection's exception.
        const int width = 512;
        const int height = 512;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        using var session = new InferenceSession(modelPath);
        byte[] rgb = new byte[width * height * 3];
        OnnxTiler.TileSplit split = OnnxTiler.TileSplit.For(width, height, 128);

        OnnxTiler.RunImplOverride.Value = (_, _) => throw new OutOfMemoryException("run seam");
        Assert.Throws<OutOfMemoryException>(() =>
            OnnxTiler.UpscaleRgb(
                rgb,
                width,
                height,
                scale,
                session,
                split,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NonFiniteOutput_IsNotCachedAsAPermanentVerdict_SoTheRetryStaysTransient()
    {
        // A page whose model returns NaN/Inf (a driver reset, a mis-executing provider) throws
        // NonFiniteModelOutputException, which is classified transient upstream precisely so the worker
        // is requeued with its spool of already-upscaled pages. Recording the failure as a permanent
        // "model unusable" verdict defeated that: the retry skipped the only candidate and fell through
        // to the generic "no model … can upscale" InvalidOperationException, which is permanent — the
        // first attempt retryable, the second terminal and the spool gone, the exact inversion the
        // transient classification was added to prevent. Both attempts must therefore surface the
        // non-finite failure; what ends a provider that really cannot run the model is the server's
        // soft-failure cap, not a process-lifetime verdict.
        const int width = 256;
        const int height = 288;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(width, height);

        var factory = new MemoryPressureSessionFactory(modelPath, scopeFailures: false);
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        var engine = new TilerFaultEngine(
            new NonFiniteModelOutputException(),
            factory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        )
        {
            FailuresLeft = int.MaxValue,
        };

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string outputPath = Path.Combine(_tempDir, $"nonfinite_{attempt}.webp");
            await Assert.ThrowsAsync<NonFiniteModelOutputException>(() =>
                engine.UpscaleFileAsync(
                    inputPath,
                    outputPath,
                    scale,
                    CompressionFormat.Webp,
                    quality: 80,
                    TestContext.Current.CancellationToken
                )
            );
            Assert.False(File.Exists(outputPath), "a failed page must not be published");
        }
    }

    /// <summary>
    /// Captures the engine's warnings so a test can assert what it decided. Only the formatted message
    /// is kept; the tests read the floor value out of it.
    /// </summary>
    private sealed class RecordingLogger : ILogger<OnnxUpscaleEngine>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private string CreateTestJpeg(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                rgb[i] = (byte)(x % 256);
                rgb[i + 1] = (byte)(y % 256);
                rgb[i + 2] = (byte)((x + y) % 256);
            }
        }

        using var image = Image.NewFromMemory(rgb, width, height, 3, Enums.BandFormat.Uchar);
        // Written under the test's own temp directory, which Dispose deletes, rather than the system
        // temp root: a run used to leave this JPEG behind in Path.GetTempPath() every time.
        string path = Path.Combine(_tempDir, $"engine_retry_{Guid.NewGuid():N}.jpg");
        image.WriteToFile(path, new VOption());
        return path;
    }

    /// <summary>
    /// Hands out real sessions and disposes the one its cache held on
    /// <see cref="InvalidateSession"/>, exactly as <see cref="OnnxSessionFactory"/> does, so the
    /// consequence of reusing the session variable across the invalidate is the same here.
    /// <para>
    /// The memory pressure is injected through <see cref="EnterInferenceScope"/> rather than by
    /// exhausting the device: it needs no GPU and no unreproducible size pressure, and the exception
    /// lands on exactly the path a real out-of-memory would. It is thrown on the second scope call,
    /// because the device probe takes the first one — throwing on the first would make the probe skip
    /// the only candidate instead of exercising the retry. The call count is asserted so that if the
    /// probe's scoping changes, this fails loudly rather than testing nothing.
    /// </para>
    /// </summary>
    private sealed class MemoryPressureSessionFactory(
        string modelPath,
        bool alwaysFails = false,
        bool scopeFailures = true
    ) : IOnnxSessionFactory
    {
        private readonly Lock _lock = new();
        private InferenceSession? _session;
        private int _invalidations;
        private int _requestedAfterInvalidation;
        private int _scopeCalls;
        private int _releaseCalls;
        private bool _releasedALiveSession;

        /// <summary>Sessions asked for after the first invalidation; 1 means the retry re-fetched.</summary>
        public int SessionsRequestedAfterInvalidation
        {
            get
            {
                lock (_lock)
                {
                    return _requestedAfterInvalidation;
                }
            }
        }

        /// <summary>How often the engine used the synchronous flush in its memory-pressure path.</summary>
        public int ReleaseCachedSessionsCalls
        {
            get
            {
                lock (_lock)
                {
                    return _releaseCalls;
                }
            }
        }

        /// <summary>Whether the synchronous flush had a live session to free (not a no-op).</summary>
        public bool ReleasedALiveSession
        {
            get
            {
                lock (_lock)
                {
                    return _releasedALiveSession;
                }
            }
        }

        public int InferenceScopeCalls => Volatile.Read(ref _scopeCalls);

        public InferenceSession GetOrCreateSession(string modelPath)
        {
            lock (_lock)
            {
                if (_invalidations > 0)
                {
                    _requestedAfterInvalidation++;
                }

                return _session ??= new InferenceSession(modelPath);
            }
        }

        public InferenceSession CreateSession(string modelPath) => new(modelPath);

        public void InvalidateSession(string modelPath)
        {
            lock (_lock)
            {
                _invalidations++;
                _session?.Dispose();
                _session = null;
            }
        }

        public void InvalidateAllSessions() => InvalidateSession(modelPath);

        /// <summary>
        /// The real factory's synchronous flush: it frees the live session before returning. Mirrored
        /// here so the retry below sees a disposed session, exactly as the production path must.
        /// </summary>
        public void ReleaseCachedSessions()
        {
            lock (_lock)
            {
                _releaseCalls++;
                _releasedALiveSession |= _session is not null;
            }

            InvalidateAllSessions();
        }

        public Shared.Configuration.GpuBackend GetEffectiveBackend() =>
            Shared.Configuration.GpuBackend.CPU;

        public IDisposable EnterInferenceScope()
        {
            // #1 is the device probe, #2 the input-metadata read (which the engine scopes too, so a
            // concurrent eviction cannot dispose the session under it), and #3 the first tile attempt
            // in the retry loop. Pinned exactly, together with the InferenceScopeCalls assertion, so
            // adding or removing a scope breaks this loudly instead of silently moving the failure to a
            // place the retry does not cover. alwaysFails keeps failing from there, which is what drives
            // the descent down to the floor.
            int call = Interlocked.Increment(ref _scopeCalls);
            // scopeFailures: false leaves the scopes clean, for the tests that inject the failure
            // somewhere else (at the tiler-call seam) and still need every scope to succeed.
            if (scopeFailures && (call == 3 || (alwaysFails && call > 3)))
            {
                throw new OutOfMemoryException("simulated device memory pressure");
            }

            return new NoOpScope();
        }

        public void Dispose() => InvalidateSession(modelPath);

        private sealed class NoOpScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    /// An engine whose tiler call fails the first <see cref="FailuresLeft"/> times, then runs the
    /// real one — the injection point for everything the tests want to throw from exactly the
    /// engine-to-tiler boundary (see <see cref="OnnxUpscaleEngine.UpscaleViaTiler"/>).
    /// </summary>
    private sealed class TilerFaultEngine(
        Exception fault,
        IOnnxSessionFactory sessionFactory,
        IOptions<UpscalerConfig> config,
        IDeviceMemoryCalibrator calibrator,
        ILogger<OnnxUpscaleEngine> logger
    ) : OnnxUpscaleEngine(sessionFactory, config, calibrator, logger)
    {
        public int FailuresLeft { get; set; }

        public int Thrown { get; private set; }

        protected internal override byte[] UpscaleViaTiler(
            byte[] rgbBytes,
            int width,
            int height,
            int scale,
            InferenceSession session,
            OnnxTiler.TileSplit split,
            CancellationToken cancellationToken
        )
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                Thrown++;
                throw fault;
            }

            return base.UpscaleViaTiler(
                rgbBytes,
                width,
                height,
                scale,
                session,
                split,
                cancellationToken
            );
        }
    }
}
