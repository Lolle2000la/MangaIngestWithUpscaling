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
    private sealed class MemoryPressureSessionFactory(string modelPath) : IOnnxSessionFactory
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
            // place the retry does not cover.
            if (Interlocked.Increment(ref _scopeCalls) == 3)
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
}
