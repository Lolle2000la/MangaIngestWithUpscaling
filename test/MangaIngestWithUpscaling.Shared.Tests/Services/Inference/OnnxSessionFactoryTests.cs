using System.Reflection;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

/// <summary>
/// These used to load a real detector model out of the gitignored <c>test_data/</c> directory, so
/// on a clean checkout every one of them silently returned and the file looked like coverage. The
/// model is now generated into a temp directory by <see cref="TinyOnnxModel"/>: nothing is skipped,
/// and what is asserted — provider selection, caching, invalidation — is genuinely exercised by a
/// session over a graph that ONNX Runtime accepts.
/// </summary>
public class OnnxSessionFactoryTests : IDisposable
{
    private readonly string _dir;

    public OnnxSessionFactoryTests() =>
        _dir = Path.Combine(Path.GetTempPath(), $"session_factory_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Writes a model once per test. Any of the generated variants would do — none of the tests below
    /// care about weights, only about a session that loads, runs and is disposable.
    /// </summary>
    private string ModelPath() => TinyOnnxModel.WriteIdentity(_dir);

    [Fact]
    [Trait("Category", "Unit")]
    public void CreateSession_WithMissingFile_ThrowsFileNotFoundException()
    {
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        Assert.Throws<FileNotFoundException>(() =>
            factory.CreateSession("non_existent_model.onnx")
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_Cpu_LoadsExistingOnnxModelSuccessfully()
    {
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(modelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
        Assert.True(session.InputMetadata.ContainsKey(TinyOnnxModel.InputName));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetOrCreateSession_ReturnsCachedInstance()
    {
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session1 = factory.GetOrCreateSession(modelPath);
        InferenceSession session2 = factory.GetOrCreateSession(modelPath);

        Assert.Same(session1, session2);
    }

    [Fact]
    public void ShouldUseCpuProvider_CoversPreferredBackendCpu()
    {
        // PreferredGpuBackend = CPU is a first-class choice and GetEffectiveBackend() reports it as
        // CPU, but it used to match no branch in TryConfigureGpuProvider and fall through to the Auto
        // discovery block, which attaches CUDA or WebGPU — so a config that asked for CPU ran on the
        // GPU, while the identity and logs said CPU. This pins the three ways to reach the CPU path.
        Assert.True(
            OnnxSessionFactory.ShouldUseCpuProvider(
                new UpscalerConfig { PreferredGpuBackend = GpuBackend.CPU }
            )
        );
        Assert.True(OnnxSessionFactory.ShouldUseCpuProvider(new UpscalerConfig { UseCPU = true }));
        Assert.True(
            OnnxSessionFactory.ShouldUseCpuProvider(new UpscalerConfig { SelectedDeviceIndex = 0 })
        );

        // A GPU preference still goes to the GPU path, so the check is not simply always true.
        Assert.False(
            OnnxSessionFactory.ShouldUseCpuProvider(
                new UpscalerConfig { PreferredGpuBackend = GpuBackend.WebGPU }
            )
        );
    }

    [Theory]
    [InlineData("CUDA execution provider is not enabled in this build.", true)]
    [InlineData("[ErrorCode:Fail] CUDA execution provider is not enabled in this build.", true)]
    [InlineData("Failed to load shared library onnxruntime_providers_cuda.so", true)]
    [InlineData("CUDA failure: an illegal memory access was encountered", false)]
    [InlineData("CUDA driver version is insufficient for CUDA runtime version", false)]
    [InlineData("CUDA failure 100: no CUDA-capable device is detected", false)]
    public void IsAbsentProviderStatus_OnlyLatchesDefinitiveAbsence(string message, bool absent)
    {
        // The CUDA probe is the one provider ORT reports through a return status instead of a
        // missing export on the shipped CPU build ("CUDA execution provider is not enabled in this
        // build."), so a plain failure treated it as transient and the provider was re-probed once a
        // minute for the process lifetime. Only the two permanent statuses latch; a driver or device
        // problem has to stay transient so a driver that becomes ready after boot is still picked up.
        Assert.Equal(absent, OnnxSessionFactory.IsAbsentProviderStatus(message));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void InvalidateAllSessions_ReplacesTheCachedSessionAndDisposesTheOldOne()
    {
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session1 = factory.GetOrCreateSession(modelPath);
        factory.InvalidateAllSessions();
        InferenceSession session2 = factory.GetOrCreateSession(modelPath);

        Assert.NotSame(session1, session2);
        // InvalidateAllSessions retires and lets the thread pool drain rather than disposing inline
        // (a caller may still hold a read scope), so the free is eventual rather than synchronous.
        // Asserting only NotSame would stay green even if the retired session were never freed at all.
        Assert.True(
            SpinWait.SpinUntil(() => IsDisposed(session1), TimeSpan.FromSeconds(10)),
            "the retired session was never disposed"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_OpenVinoBackend_FallsBackCleanlyToCpu()
    {
        string modelPath = ModelPath();

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.OpenVINO,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        // When OpenVINO hardware is not available on this test host, it must cleanly fallback to CPU without throwing.
        using InferenceSession session = factory.CreateSession(modelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_MigraphxBackend_CreatesOrFallsBackSuccessfully()
    {
        string modelPath = ModelPath();

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.MIGraphX,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(modelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_WebGpuBackend_CreatesOrFallsBackSuccessfully()
    {
        string modelPath = ModelPath();

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.WebGPU,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(modelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_AutoBackend_CreatesOrFallsBackSuccessfully()
    {
        string modelPath = ModelPath();

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.Auto,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(modelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_ConsultsTheSameResolutionGetEffectiveBackendReports()
    {
        // CreateSession used to run its own Auto discovery, so it could attach a provider that
        // GetEffectiveBackend had rejected or not yet accepted, while the engine identity and the
        // calibration fingerprint described the other one — pages from one backend attributed to
        // another and blended into a chapter. It now goes through GetEffectiveBackend, the single
        // resolution the identity records. The assertion is on that resolution having been consulted
        // and cached, which holds whatever this host resolves to; only a real accelerator can make
        // the provider a session actually attaches observable.
        string modelPath = ModelPath();
        var config = Options.Create(
            new UpscalerConfig { PreferredGpuBackend = GpuBackend.Auto, SelectedDeviceIndex = 1 }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
        Assert.Null(GetPrivateField<GpuBackend?>(factory, "_resolvedBackend"));

        using InferenceSession session = factory.CreateSession(modelPath);

        Assert.NotNull(session);
        // Without the fix CreateSession never consults GetEffectiveBackend, so the field stays null and
        // the backend a session runs on can differ from the one the identity advertises.
        Assert.NotNull(GetPrivateField<GpuBackend?>(factory, "_resolvedBackend"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetEffectiveBackend_RetriesAResolutionThatSkippedAHigherPriorityProvider()
    {
        // A transient probe failure on a higher-priority provider (CUDA on Linux, WebGPU on Windows)
        // lets a lower one resolve. That resolution must stay provisional: the skipped provider can
        // recover, and CreateSession now attaches whatever this resolution returns, so latching the
        // lower one would pin the session, the engine identity and the calibration fingerprint to it
        // for the process lifetime.
        var config = Options.Create(
            new UpscalerConfig { PreferredGpuBackend = GpuBackend.Auto, SelectedDeviceIndex = 1 }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
        using var reference = new OnnxSessionFactory(
            config,
            NullLogger<OnnxSessionFactory>.Instance
        );

        // Auto never resolves to OpenVINO on any platform, so a cached OpenVINO value can only come
        // from the cache and never from a re-probe — which makes the test independent of whether this
        // host has an accelerator.
        SeedResolution(
            factory,
            GpuBackend.OpenVINO,
            transient: true,
            resolvedAt: DateTime.UtcNow - TimeSpan.FromHours(1)
        );

        GpuBackend resolved = factory.GetEffectiveBackend();

        // Without the fix the non-CPU cache hit returns the latched OpenVINO; with it the probe is
        // retried and the host's real resolution (never OpenVINO) comes back.
        Assert.NotEqual(GpuBackend.OpenVINO, resolved);
        Assert.Equal(reference.GetEffectiveBackend(), resolved);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetEffectiveBackend_KeepsATransientResolutionUntilTheRetryIntervalElapses()
    {
        // Within the retry interval the cached value is served, so a persistently failing provider is
        // not re-probed on every call (the resolution is real work and once ran once per page).
        var config = Options.Create(
            new UpscalerConfig { PreferredGpuBackend = GpuBackend.Auto, SelectedDeviceIndex = 1 }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
        SeedResolution(factory, GpuBackend.OpenVINO, transient: true, resolvedAt: DateTime.UtcNow);

        Assert.Equal(GpuBackend.OpenVINO, factory.GetEffectiveBackend());
    }

    private static void SeedResolution(
        OnnxSessionFactory factory,
        GpuBackend backend,
        bool transient,
        DateTime resolvedAt
    )
    {
        SetPrivateField(factory, "_resolvedBackend", backend);
        SetPrivateField(factory, "_backendResolutionWasTransient", transient);
        SetPrivateField(factory, "_lastBackendResolutionUtc", resolvedAt);
    }

    private static void SetPrivateField<T>(OnnxSessionFactory factory, string name, T value) =>
        (
            typeof(OnnxSessionFactory).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new InvalidOperationException(
                $"OnnxSessionFactory.{name} was not found; the test probe needs updating."
            )
        ).SetValue(factory, value);

    private static T GetPrivateField<T>(OnnxSessionFactory factory, string name) =>
        (T)
            (
                typeof(OnnxSessionFactory).GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic
                )
                ?? throw new InvalidOperationException(
                    $"OnnxSessionFactory.{name} was not found; the test probe needs updating."
                )
            ).GetValue(factory)!;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrCreateSession_EvictsTheLeastRecentlyUsedSessionAtCapacity()
    {
        // MaxCachedSessions is 1, so the second model displaces the first instead of joining it.
        // At that capacity recency ordering cannot be distinguished from any other eviction policy —
        // the single resident entry is the least recently used by definition — so what is pinned
        // beyond the capacity behaviour is that the evicted sessions are actually disposed. The
        // factory retires them and drains the retiral on the thread pool, because disposing inline
        // would need the write lock from inside the evicting caller's read scope, so their release
        // has to be waited for rather than inspected immediately.
        string first = TinyOnnxModel.WriteNearestUpscaler(_dir, 2, fileName: "first.onnx");
        string second = TinyOnnxModel.WriteNearestUpscaler(_dir, 3, fileName: "second.onnx");
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession firstSession = factory.GetOrCreateSession(first);
        InferenceSession secondSession = factory.GetOrCreateSession(second);

        // Only the second one is resident, so the first model has to be loaded again.
        Assert.NotSame(firstSession, secondSession);
        InferenceSession reloaded = factory.GetOrCreateSession(first);
        Assert.NotSame(firstSession, reloaded);

        // The resident model is served from the cache rather than reloaded.
        Assert.Same(reloaded, factory.GetOrCreateSession(first));

        // The first session was evicted when the second was fetched and the second when the first was
        // re-fetched; both have to be released. Asserted on a bool so a failure never formats a
        // disposed session, whose ToString dereferences the native handle.
        Assert.True(
            await WaitUntilDisposedAsync(firstSession, TestContext.Current.CancellationToken),
            "the evicted session was never disposed"
        );
        Assert.True(
            await WaitUntilDisposedAsync(secondSession, TestContext.Current.CancellationToken),
            "the evicted session was never disposed"
        );
    }

    /// <summary>
    /// Waits for ONNX Runtime to release <paramref name="session"/>'s native handle. The only public
    /// disposed state <c>InferenceSession</c> offers is <see cref="IDisposable.Dispose"/> itself, and
    /// it does not guard its members afterwards, so the native handle that <c>Dispose</c> zeroes is
    /// the reliable signal. It is read by reflection because the field is private.
    /// </summary>
    private static async Task<bool> WaitUntilDisposedAsync(
        InferenceSession session,
        CancellationToken cancellationToken
    )
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            if (IsDisposed(session))
            {
                return true;
            }

            await Task.Delay(10, cancellationToken);
        }

        return IsDisposed(session);
    }

    private static bool IsDisposed(InferenceSession session)
    {
        FieldInfo handle =
            typeof(InferenceSession).GetField(
                "_nativeHandle",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new InvalidOperationException(
                "InferenceSession._nativeHandle was not found; the disposal probe needs updating."
            );

        return (IntPtr)handle.GetValue(session)! == IntPtr.Zero;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SessionIsNotDisposedByInvalidationWhileItsScopeIsHeld()
    {
        // The engine takes the scope BEFORE the fetch, so an eviction or invalidation on another
        // thread cannot free the session between the lookup and the Run. This proves the deferral:
        // while the scope is held the session must still be runnable, and only after the scope drains
        // may the invalidation dispose it.
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session;
        using (factory.EnterInferenceScope())
        {
            session = factory.GetOrCreateSession(modelPath);

            // Same thread, so this cannot deadlock on the scope; a concurrent invalidate takes the
            // same path. The disposal must be deferred, not performed here.
            factory.InvalidateSession(modelPath);

            Assert.False(
                IsDisposed(session),
                "the invalidated session was disposed while its scope was still held"
            );
        }

        // Once the scope is gone the deferred drain may dispose it.
        Assert.True(
            await WaitUntilDisposedAsync(session, TestContext.Current.CancellationToken),
            "the invalidated session was never disposed after its scope ended"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ReleaseCachedSessions_DisposesTheCachedSessionBeforeItReturns()
    {
        // InvalidateAllSessions retires and lets the thread pool drain, so the memory is not
        // necessarily free when it returns. ReleaseCachedSessions has to be synchronous: the OOM
        // retry and the chapter boundary depend on the device memory being free before their next
        // scope, which the deferred drain would otherwise block behind.
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session = factory.GetOrCreateSession(modelPath);
        Assert.False(IsDisposed(session));

        factory.ReleaseCachedSessions();

        Assert.True(
            IsDisposed(session),
            "ReleaseCachedSessions returned with the session still resident"
        );
        Assert.NotSame(session, factory.GetOrCreateSession(modelPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DrainRetired_AfterTheFactoryIsDisposed_FreesItsBatchWithoutThrowing()
    {
        // Dispose() disposes _activeExecutionLock once it has taken the write lock. A background
        // drain that snapshotted its batch first and reached EnterWriteLock afterwards used to throw
        // ObjectDisposedException on a pool thread — an unhandled exception, i.e. a non-graceful
        // process shutdown — and leave the batch undisposed. The drain runs after Dispose here, with
        // a batch only the drain owns (the session was never cached, so Dispose did not take it).
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession batch = factory.CreateSession(modelPath);
        factory.Dispose();

        RetiredSetOf(factory).Add(batch);

        InvokeDrainRetired(factory); // Must not surface ObjectDisposedException.

        Assert.True(
            IsDisposed(batch),
            "the drain gave up and leaked the batch it had snapshotted before Dispose won"
        );
    }

    private static HashSet<InferenceSession> RetiredSetOf(OnnxSessionFactory factory) =>
        (HashSet<InferenceSession>)(
            typeof(OnnxSessionFactory)
                .GetField("_retired", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(factory)
            ?? throw new InvalidOperationException(
                "OnnxSessionFactory._retired was not found; the disposal probe needs updating."
            )
        );

    [Fact]
    [Trait("Category", "Unit")]
    public void DemoteBackend_FromCpu_HasNowhereToFallBackTo()
    {
        // CPU is the end of the ladder: reporting a different provider here would make the engine
        // retry the page forever on the same provider.
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        Assert.Equal(GpuBackend.CPU, factory.DemoteBackend(GpuBackend.CPU));
        Assert.Equal(GpuBackend.CPU, factory.GetEffectiveBackend());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DemoteBackend_OfAProviderThatIsNotInUse_KeepsTheResolution()
    {
        // Only the provider actually in use can be abandoned; a caller that names another one (a
        // stale resolution, a race) must not cost the process its working backend.
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
        GpuBackend resolved = factory.GetEffectiveBackend();
        GpuBackend notInUse =
            resolved == GpuBackend.WebGPU ? GpuBackend.DirectML : GpuBackend.WebGPU;

        Assert.Equal(resolved, factory.DemoteBackend(notInUse));
        Assert.Equal(resolved, factory.GetEffectiveBackend());
    }

    private static void InvokeDrainRetired(OnnxSessionFactory factory) =>
        (
            typeof(OnnxSessionFactory).GetMethod(
                "DrainRetired",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new InvalidOperationException(
                "OnnxSessionFactory.DrainRetired was not found; the disposal probe needs updating."
            )
        ).Invoke(factory, null);
}
