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
    private static readonly string TestModelPath = SplitDetectionLayout.ResolveModelPath();

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
}
