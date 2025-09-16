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
    public async Task DownloadModelsIfNecessary_ShouldCallPythonServiceForModelDownload()
    {
        // Arrange
        var cancellationToken = CancellationToken.None;
        
        // Setup Python service mock
        _mockPythonService.RunPythonScript(
            Arg.Any<string>(),
            Arg.Any<string>(),
            cancellationToken,
            Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult("Models downloaded successfully"));

        // Act
        await _upscaler.DownloadModelsIfNecessary(cancellationToken);

        // Assert
        await _mockPythonService.Received().RunPythonScript(
            Arg.Any<string>(),
            Arg.Any<string>(),
            cancellationToken,
            Arg.Any<TimeSpan?>());
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
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken));
        
        Assert.Contains("Input file not found", exception.Message);
    }
}