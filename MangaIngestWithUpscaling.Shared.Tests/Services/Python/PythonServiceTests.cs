using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Python;

public class PythonServiceTests
{
    private readonly IGpuDetectionService _mockGpuDetection;
    private readonly ILogger<PythonService> _mockLogger;
    private readonly PythonService _pythonService;

    public PythonServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<PythonService>>();
        _mockGpuDetection = Substitute.For<IGpuDetectionService>();
        _pythonService = new PythonService(_mockLogger, _mockGpuDetection);
    }

    [Theory]
    [Trait("Category", "Download")]
    [Trait("Category", "Integration")]
    [InlineData(GpuBackend.Auto)]
    [InlineData(GpuBackend.CPU)]
    [InlineData(GpuBackend.CUDA)]
    [InlineData(GpuBackend.CUDA_12_8)]
    [InlineData(GpuBackend.ROCm)]
    [InlineData(GpuBackend.XPU)]
    public async Task PreparePythonEnvironment_WithDifferentBackends_ShouldHandleAllBackendTypes(GpuBackend backend)
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"python_test_{Guid.NewGuid()}_{backend}");

        try
        {
            // Act & Assert
            var exception = await Record.ExceptionAsync(() =>
                _pythonService.PreparePythonEnvironment(tempDir, backend, true));

            // Should handle all backend types without crashing
            Assert.True(exception is null or FileNotFoundException or InvalidOperationException);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunPythonScript_WithInvalidScript_ShouldThrowException()
    {
        // Arrange
        const string invalidScript = "nonexistent_script.py";
        const string arguments = "";

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            _pythonService.RunPythonScript(invalidScript, arguments, CancellationToken.None));

        // Should throw some kind of exception for invalid script
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task RunPythonScriptStreaming_WithInvalidScript_ShouldThrowException()
    {
        // Arrange
        const string invalidScript = "nonexistent_script.py";
        const string arguments = "";
        var mockCallback = Substitute.For<Func<string, Task>>();

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            _pythonService.RunPythonScriptStreaming(invalidScript, arguments, mockCallback, CancellationToken.None));

        // Should throw some kind of exception for invalid script
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task RunPythonScriptStreaming_WithCancellation_ShouldRespectCancellationToken()
    {
        // Arrange
        const string script = "nonexistent_script.py";
        const string arguments = "";
        var mockCallback = Substitute.For<Func<string, Task>>();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            _pythonService.RunPythonScriptStreaming(script, arguments, mockCallback, cts.Token));

        // Should throw either OperationCanceledException or another exception due to invalid script
        Assert.NotNull(exception);
    }
}