using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class MangaJaNaiUpscalerTests : IDisposable
{
    private readonly IPythonService _mockPythonService;
    private readonly ILogger<MangaJaNaiUpscaler> _mockLogger;
    private readonly IOptions<UpscalerConfig> _mockConfig;
    private readonly IFileSystem _mockFileSystem;
    private readonly IMetadataHandlingService _mockMetadataHandling;
    private readonly IUpscalerJsonHandlingService _mockJsonHandling;
    private readonly IImageResizeService _mockImageResize;
    private readonly MangaJaNaiUpscaler _upscaler;
    private readonly string _tempDir;

    public MangaJaNaiUpscalerTests()
    {
        _mockPythonService = Substitute.For<IPythonService>();
        _mockLogger = Substitute.For<ILogger<MangaJaNaiUpscaler>>();
        _mockFileSystem = Substitute.For<IFileSystem>();
        _mockMetadataHandling = Substitute.For<IMetadataHandlingService>();
        _mockJsonHandling = Substitute.For<IUpscalerJsonHandlingService>();
        _mockImageResize = Substitute.For<IImageResizeService>();
        
        var config = new UpscalerConfig
        {
            UseFp16 = true,
            UseCPU = false,
            SelectedDeviceIndex = 0,
            RemoteOnly = false,
            PreferredGpuBackend = GpuBackend.Auto
        };
        _mockConfig = Substitute.For<IOptions<UpscalerConfig>>();
        _mockConfig.Value.Returns(config);

        _tempDir = Path.Combine(Path.GetTempPath(), $"upscaler_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _upscaler = new MangaJaNaiUpscaler(
            _mockPythonService,
            _mockLogger,
            _mockConfig,
            _mockFileSystem,
            _mockMetadataHandling,
            _mockJsonHandling,
            _mockImageResize
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task Upscale_ValidInputs_ShouldCallPythonServiceWithCorrectParameters()
    {
        // Arrange
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Setup file system mocks
        _mockFileSystem.FileExists(inputPath).Returns(true);
        _mockFileSystem.CreateTempDirectory().Returns(_tempDir);
        
        // Setup Python service mock to simulate successful upscaling
        var pythonEnvironment = new PythonEnvironment("test-env", "test-python", true);
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), Arg.Any<GpuBackend>(), Arg.Any<bool>())
            .Returns(Task.FromResult(pythonEnvironment));
        
        _mockPythonService.RunPythonScriptStreaming(
            Arg.Any<PythonEnvironment>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken?>(),
            Arg.Any<TimeSpan?>())
            .Returns(Task.CompletedTask);

        // Setup JSON handling mock
        _mockJsonHandling.CreateUpscalerConfig(Arg.Any<UpscalerProfile>(), Arg.Any<UpscalerConfig>())
            .Returns("test-config.json");

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        await _mockPythonService.Received(1).PreparePythonEnvironment(
            Arg.Any<string>(), 
            _mockConfig.Value.PreferredGpuBackend, 
            Arg.Any<bool>());

        await _mockPythonService.Received(1).RunPythonScriptStreaming(
            pythonEnvironment,
            Arg.Any<string>(),
            Arg.Is<string>(args => args.Contains(inputPath) && args.Contains(outputPath)),
            Arg.Any<Func<string, Task>>(),
            cancellationToken,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Upscale_WithProgress_ShouldReportProgressUpdates()
    {
        // Arrange
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;
        var progressReports = new List<UpscaleProgress>();
        var progress = Substitute.For<IProgress<UpscaleProgress>>();
        progress.When(p => p.Report(Arg.Any<UpscaleProgress>()))
            .Do(callInfo => progressReports.Add(callInfo.Arg<UpscaleProgress>()));

        // Setup file system mocks
        _mockFileSystem.FileExists(inputPath).Returns(true);
        _mockFileSystem.CreateTempDirectory().Returns(_tempDir);
        
        // Setup Python service mock
        var pythonEnvironment = new PythonEnvironment("test-env", "test-python", true);
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), Arg.Any<GpuBackend>(), Arg.Any<bool>())
            .Returns(Task.FromResult(pythonEnvironment));
        
        // Setup streaming to simulate progress updates
        _mockPythonService.RunPythonScriptStreaming(
            Arg.Any<PythonEnvironment>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken?>(),
            Arg.Any<TimeSpan?>())
            .Returns(callInfo =>
            {
                var onStdout = callInfo.Arg<Func<string, Task>>();
                // Simulate progress output from Python script
                return Task.Run(async () =>
                {
                    await onStdout("Processing: 10%");
                    await onStdout("Processing: 50%");
                    await onStdout("Processing: 100%");
                });
            });

        // Setup JSON handling mock
        _mockJsonHandling.CreateUpscalerConfig(Arg.Any<UpscalerProfile>(), Arg.Any<UpscalerConfig>())
            .Returns("test-config.json");

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, progress, cancellationToken);

        // Assert
        progress.Received().Report(Arg.Any<UpscaleProgress>());
        
        // Verify Python service was called correctly
        await _mockPythonService.Received(1).RunPythonScriptStreaming(
            pythonEnvironment,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            cancellationToken,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Upscale_PythonScriptFails_ShouldThrowException()
    {
        // Arrange
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Setup file system mocks
        _mockFileSystem.FileExists(inputPath).Returns(true);
        _mockFileSystem.CreateTempDirectory().Returns(_tempDir);
        
        // Setup Python service to fail
        var pythonEnvironment = new PythonEnvironment("test-env", "test-python", true);
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), Arg.Any<GpuBackend>(), Arg.Any<bool>())
            .Returns(Task.FromResult(pythonEnvironment));
        
        _mockPythonService.RunPythonScriptStreaming(
            Arg.Any<PythonEnvironment>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken?>(),
            Arg.Any<TimeSpan?>())
            .Throws(new InvalidOperationException("Python script failed"));

        // Setup JSON handling mock
        _mockJsonHandling.CreateUpscalerConfig(Arg.Any<UpscalerProfile>(), Arg.Any<UpscalerConfig>())
            .Returns("test-config.json");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken));
        
        Assert.Equal("Python script failed", exception.Message);
    }

    [Fact]
    public async Task Upscale_InputFileNotExists_ShouldThrowFileNotFoundException()
    {
        // Arrange
        const string inputPath = "nonexistent.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Setup file system mock to return false for file existence
        _mockFileSystem.FileExists(inputPath).Returns(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken));
        
        Assert.Contains(inputPath, exception.Message);
    }

    [Fact]
    public async Task Upscale_CancellationRequested_ShouldPassCancellationToPythonService()
    {
        // Arrange
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel(); // Cancel immediately

        // Setup file system mocks
        _mockFileSystem.FileExists(inputPath).Returns(true);
        _mockFileSystem.CreateTempDirectory().Returns(_tempDir);
        
        // Setup Python service mock
        var pythonEnvironment = new PythonEnvironment("test-env", "test-python", true);
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), Arg.Any<GpuBackend>(), Arg.Any<bool>())
            .Returns(Task.FromResult(pythonEnvironment));
        
        _mockPythonService.RunPythonScriptStreaming(
            Arg.Any<PythonEnvironment>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken?>(),
            Arg.Any<TimeSpan?>())
            .Throws(new OperationCanceledException());

        // Setup JSON handling mock
        _mockJsonHandling.CreateUpscalerConfig(Arg.Any<UpscalerProfile>(), Arg.Any<UpscalerConfig>())
            .Returns("test-config.json");

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _upscaler.Upscale(inputPath, outputPath, profile, cancellationTokenSource.Token));
        
        // Verify cancellation token was passed to Python service
        await _mockPythonService.Received(1).RunPythonScriptStreaming(
            pythonEnvironment,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            cancellationTokenSource.Token,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task DownloadModelsIfNecessary_ShouldCallPythonServiceForModelDownload()
    {
        // Arrange
        var cancellationToken = CancellationToken.None;
        
        // Setup Python service mock
        var pythonEnvironment = new PythonEnvironment("test-env", "test-python", true);
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), Arg.Any<GpuBackend>(), Arg.Any<bool>())
            .Returns(Task.FromResult(pythonEnvironment));
        
        _mockPythonService.RunPythonScript(
            pythonEnvironment,
            Arg.Any<string>(),
            Arg.Any<string>(),
            cancellationToken,
            Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult("Models downloaded successfully"));

        // Act
        await _upscaler.DownloadModelsIfNecessary(cancellationToken);

        // Assert
        await _mockPythonService.Received(1).PreparePythonEnvironment(
            Arg.Any<string>(),
            _mockConfig.Value.PreferredGpuBackend,
            Arg.Any<bool>());
        
        await _mockPythonService.Received(1).RunPythonScript(
            pythonEnvironment,
            Arg.Any<string>(),
            Arg.Any<string>(),
            cancellationToken,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task DownloadModelsIfNecessary_PythonEnvironmentFails_ShouldThrowException()
    {
        // Arrange
        var cancellationToken = CancellationToken.None;
        
        // Setup Python service to fail environment preparation
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), Arg.Any<GpuBackend>(), Arg.Any<bool>())
            .Throws(new InvalidOperationException("Failed to prepare Python environment"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _upscaler.DownloadModelsIfNecessary(cancellationToken));
        
        Assert.Equal("Failed to prepare Python environment", exception.Message);
    }

    [Theory]
    [InlineData(GpuBackend.Auto)]
    [InlineData(GpuBackend.Cuda)]
    [InlineData(GpuBackend.Rocm)]
    [InlineData(GpuBackend.Xpu)]
    public async Task Upscale_DifferentGpuBackends_ShouldPassCorrectBackendToPythonService(GpuBackend backend)
    {
        // Arrange
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Update config with specific backend
        var config = new UpscalerConfig
        {
            UseFp16 = true,
            UseCPU = false,
            SelectedDeviceIndex = 0,
            RemoteOnly = false,
            PreferredGpuBackend = backend
        };
        _mockConfig.Value.Returns(config);

        // Setup file system mocks
        _mockFileSystem.FileExists(inputPath).Returns(true);
        _mockFileSystem.CreateTempDirectory().Returns(_tempDir);
        
        // Setup Python service mock
        var pythonEnvironment = new PythonEnvironment("test-env", "test-python", true);
        _mockPythonService.PreparePythonEnvironment(Arg.Any<string>(), backend, Arg.Any<bool>())
            .Returns(Task.FromResult(pythonEnvironment));
        
        _mockPythonService.RunPythonScriptStreaming(
            Arg.Any<PythonEnvironment>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken?>(),
            Arg.Any<TimeSpan?>())
            .Returns(Task.CompletedTask);

        // Setup JSON handling mock
        _mockJsonHandling.CreateUpscalerConfig(Arg.Any<UpscalerProfile>(), Arg.Any<UpscalerConfig>())
            .Returns("test-config.json");

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        await _mockPythonService.Received(1).PreparePythonEnvironment(
            Arg.Any<string>(), 
            backend, 
            Arg.Any<bool>());
    }
}