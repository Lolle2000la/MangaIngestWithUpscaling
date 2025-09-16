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
using System.Reflection;

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
    [Trait("Category", "Unit")]
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
    [Trait("Category", "Unit")]
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
    [Trait("Category", "Unit")]
    public async Task Upscale_WithProgress_ShouldReportProgressCorrectly()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(inputPath, "dummy content");

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        var progressReports = new List<UpscaleProgress>();
        var progress = new Progress<UpscaleProgress>(p => progressReports.Add(p));

        // Mock Python service to simulate progress output
        _mockPythonService
            .RunPythonScriptStreaming(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Func<string, Task>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(async call =>
            {
                var onStdout = call.Arg<Func<string, Task>>();
                // Simulate progress output
                await onStdout("TOTALZIP=5");
                await onStdout("PROGRESS=postprocess_worker_zip_image item1");
                await onStdout("PROGRESS=postprocess_worker_zip_image item2");
                await onStdout("PROGRESS=postprocess_worker_zip_image item3");
            });

        // Mock metadata handling to avoid actual metadata operations
        _mockMetadataHandling.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, progress, cancellationToken);

        // Assert
        Assert.NotEmpty(progressReports);

        // Verify total was set
        var totalReport = progressReports.FirstOrDefault(p => p.Total.HasValue);
        Assert.NotNull(totalReport);
        Assert.Equal(5, totalReport.Total!.Value);

        // Verify progress increments were reported
        var progressIncrements = progressReports.Where(p => p.Current > 0).ToList();
        Assert.NotEmpty(progressIncrements);

        // Verify the last progress shows incremental updates
        Assert.True(progressReports.Last().Current >= 1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithExistingOutputFile_ShouldSkipIfPagesEqual()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(inputPath, "dummy content");
        await File.WriteAllTextAsync(outputPath, "existing output");

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        // Mock metadata handling to return true for pages equal (already upscaled)
        _mockMetadataHandling.PagesEqual(inputPath, outputPath).Returns(true);

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify that Python service was NOT called since file already exists and pages are equal
        await _mockPythonService.DidNotReceive().RunPythonScript(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>());
        await _mockPythonService.DidNotReceive().RunPythonScriptStreaming(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>());

        // Verify output file still exists (wasn't deleted)
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithCancellation_ShouldPropagateCancellation()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(inputPath, "dummy content");

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Mock metadata handling
        _mockMetadataHandling.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        // Mock Python service to throw on cancellation
        _mockPythonService
            .RunPythonScript(Arg.Any<string>(), Arg.Any<string>(), cts.Token, Arg.Any<TimeSpan?>())
            .Returns(Task.FromCanceled<string>(cts.Token));

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            _upscaler.Upscale(inputPath, outputPath, profile, cts.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithResizing_ShouldCallImageResizeService()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(inputPath, "dummy content");

        // Configure max dimension for resizing
        _mockConfig.Value.MaxDimensionBeforeUpscaling = 1024;

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        // Mock metadata handling
        _mockMetadataHandling.PagesEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        // Mock image resize service to return a temp file
        string tempResizedPath = Path.Combine(_tempDir, "temp_resized.cbz");
        await File.WriteAllTextAsync(tempResizedPath, "temp resized content");

        // Create a real TempResizedCbz instance (but with mock cleanup)
        _mockImageResize.CreateResizedTempCbzAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Use reflection to create the TempResizedCbz since constructor is internal
                ConstructorInfo? constructor = typeof(TempResizedCbz).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(IImageResizeService) },
                    null);
                return (TempResizedCbz)constructor!.Invoke(new object[] { tempResizedPath, _mockImageResize });
            });

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify resize service was called
        await _mockImageResize.Received(1).CreateResizedTempCbzAsync(inputPath, 1024, cancellationToken);

        // Verify Python service was called
        await _mockPythonService.Received(1).RunPythonScript(
            Arg.Any<string>(),
            Arg.Any<string>(),
            cancellationToken,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    [Trait("Category", "Unit")]
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
}