using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Xunit;

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
    [Trait("Category", "Integration")]
    public void InvalidateAllSessions_RemovesAndDisposesCachedSessions()
    {
        string modelPath = ModelPath();
        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session1 = factory.GetOrCreateSession(modelPath);
        factory.InvalidateAllSessions();
        InferenceSession session2 = factory.GetOrCreateSession(modelPath);

        Assert.NotSame(session1, session2);
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
    public void GetOrCreateSession_EvictsTheLeastRecentlyUsedSessionAtCapacity()
    {
        // MaxCachedSessions is 1, so the second model displaces the first instead of joining it.
        // This is what keeps split detection and upscaling from holding two models on a
        // VRAM-limited card at once, and the eviction disposes the outgoing session while holding
        // the write lock — the part of the factory with the most moving parts to get wrong.
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

        // Never assert on an instance the factory has evicted: xUnit formats the arguments of a
        // failing assertion, and InferenceSession.ToString() dereferences the native session, so a
        // disposed object turns a test failure into a crash instead of a message.
        Assert.NotNull(secondSession);
    }
}
