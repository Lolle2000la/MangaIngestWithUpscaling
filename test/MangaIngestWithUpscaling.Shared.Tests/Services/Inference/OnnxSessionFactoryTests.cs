using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

public class OnnxSessionFactoryTests
{
    private static readonly string? TestModelPath = ResolveStableTestModelPath();

    private static string? ResolveStableTestModelPath()
    {
        string currentTestData = Path.Combine(
            Directory.GetCurrentDirectory(),
            "test_data",
            "models",
            "page_break_detector.onnx"
        );
        if (File.Exists(currentTestData))
        {
            return currentTestData;
        }

        string directModelsPath = Path.Combine(
            AppContext.BaseDirectory,
            "models",
            "page_break_detector.onnx"
        );
        if (File.Exists(directModelsPath))
        {
            return directModelsPath;
        }

        return null;
    }

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
        if (!File.Exists(TestModelPath))
        {
            // Skip if model not deployed to output dir in this test run
            return;
        }

        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(TestModelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
        Assert.True(session.InputMetadata.ContainsKey("input"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetOrCreateSession_ReturnsCachedInstance()
    {
        if (!File.Exists(TestModelPath))
        {
            return;
        }

        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session1 = factory.GetOrCreateSession(TestModelPath);
        InferenceSession session2 = factory.GetOrCreateSession(TestModelPath);

        Assert.Same(session1, session2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void InvalidateAllSessions_RemovesAndDisposesCachedSessions()
    {
        if (!File.Exists(TestModelPath))
        {
            return;
        }

        var config = Options.Create(new UpscalerConfig { UseCPU = true });
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        InferenceSession session1 = factory.GetOrCreateSession(TestModelPath);
        factory.InvalidateAllSessions();
        InferenceSession session2 = factory.GetOrCreateSession(TestModelPath);

        Assert.NotSame(session1, session2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_OpenVinoBackend_FallsBackCleanlyToCpu()
    {
        if (!File.Exists(TestModelPath))
        {
            return;
        }

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
        using InferenceSession session = factory.CreateSession(TestModelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_MigraphxBackend_CreatesOrFallsBackSuccessfully()
    {
        if (!File.Exists(TestModelPath))
        {
            return;
        }

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.MIGraphX,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(TestModelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_WebGpuBackend_CreatesOrFallsBackSuccessfully()
    {
        if (!File.Exists(TestModelPath))
        {
            return;
        }

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.WebGPU,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(TestModelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CreateSession_AutoBackend_CreatesOrFallsBackSuccessfully()
    {
        if (!File.Exists(TestModelPath))
        {
            return;
        }

        var config = Options.Create(
            new UpscalerConfig
            {
                UseCPU = false,
                PreferredGpuBackend = GpuBackend.Auto,
                SelectedDeviceIndex = 1,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);

        using InferenceSession session = factory.CreateSession(TestModelPath);
        Assert.NotNull(session);
        Assert.NotEmpty(session.InputMetadata);
    }
}
