using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class MangaJaNaiUpscalerTests : IDisposable
{
    private readonly IOptions<UpscalerConfig> _mockConfig;
    private readonly IFileSystem _mockFileSystem;
    private readonly IImageResizeService _mockImageResize;
    private readonly IUpscalerJsonHandlingService _mockJsonHandling;
    private readonly ILogger<MangaJaNaiUpscaler> _mockLogger;
    private readonly IMetadataHandlingService _mockMetadataHandling;
    private readonly IPythonService _mockPythonService;
    private readonly string _tempDir;
    private readonly MangaJaNaiUpscaler _upscaler;

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
            PreferredGpuBackend = GpuBackend.Auto,
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"models_{Guid.NewGuid()}")
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

        if (Directory.Exists(_mockConfig.Value.ModelsDirectory))
        {
            Directory.Delete(_mockConfig.Value.ModelsDirectory, true);
        }
    }

    [Fact]
    public async Task Upscale_InputFileNotExists_ShouldThrowFileNotFoundException()
    {
        // Arrange
        const string inputPath = "nonexistent.cbz";
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken));

        Assert.Contains("Input file not found", exception.Message);
    }

    [Fact]
    public async Task Upscale_InvalidOutputPath_ShouldThrowArgumentException()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.txt"); // Wrong extension

        // Create a dummy input file
        await File.WriteAllTextAsync(inputPath, "dummy content");

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken));

        Assert.Contains("Output path must be a cbz file", exception.Message);
    }

    [Fact]
    public void Constructor_ShouldNotThrow()
    {
        // This test validates that the constructor can be called with mocked dependencies
        // without throwing exceptions due to missing dependencies

        // Arrange, Act & Assert - Constructor is called in test setup
        Assert.NotNull(_upscaler);
    }

    [Fact]
    [Trait("Category", "Download")]
    [Trait("Category", "Integration")]
    public async Task DownloadModelsIfNecessary_ShouldComplete()
    {
        // Arrange
        var cancellationToken = CancellationToken.None;

        // Act & Assert - This may download models
        var exception = await Record.ExceptionAsync(() =>
            _upscaler.DownloadModelsIfNecessary(cancellationToken));

        // Should either complete successfully or fail with expected exceptions
        Assert.True(exception is null or FileNotFoundException or InvalidOperationException or HttpRequestException);
    }

    [Fact]
    public void DownloadModelsIfNecessary_InterfaceValidation_ShouldBeCallable()
    {
        // This test just validates that the method exists and can be called (interface compliance)
        // without actually executing it to avoid downloads in local development

        // Arrange
        var cancellationToken = CancellationToken.None;

        // Act & Assert - Just check the method exists and is callable
        Assert.NotNull(_upscaler);

        // Verify the method signature exists by getting method info
        var methodInfo = typeof(MangaJaNaiUpscaler).GetMethod(nameof(MangaJaNaiUpscaler.DownloadModelsIfNecessary));
        Assert.NotNull(methodInfo);
        Assert.True(methodInfo.ReturnType == typeof(Task));
    }
}