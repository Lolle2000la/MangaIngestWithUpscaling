using System.Reflection;
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

        if (TestContext.Current.TestCase is null)
        {
            throw new InvalidOperationException(
                "TestContext.Current.TestCase is null. Cannot proceed with test setup."
            );
        }

        var config = new UpscalerConfig
        {
            UseFp16 = true,
            UseCPU = false,
            SelectedDeviceIndex = 0,
            RemoteOnly = false,
            PreferredGpuBackend = GpuBackend.Auto,
            ModelsDirectory = Path.Combine(
                Path.GetTempPath(),
                $"upscaler_test_{TestContext.Current.TestCase.TestCaseDisplayName}"
            ),
            ImageFormatConversionRules = [],
        };
        _mockConfig = Substitute.For<IOptions<UpscalerConfig>>();
        _mockConfig.Value.Returns(config);

        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"upscaler_test_{TestContext.Current.TestCase.TestCaseDisplayName}"
        );
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
            Quality = 80,
        };
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken)
        );

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
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken)
        );

        Assert.Contains("Output path must be a cbz file", exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithProgress_ShouldReportProgressCorrectly()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
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
                Arg.Any<TimeSpan?>()
            )
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
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, progress, cancellationToken);

        // NOTE: If this test continues to fail, take into account timing issues
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(progressReports);

        // Verify total was set
        var totalReport = progressReports.FirstOrDefault(p => p.Total.HasValue);
        Assert.NotNull(totalReport);
        Assert.Equal(5, totalReport.Total!.Value);

        // Verify progress increments were reported
        var progressIncrements = progressReports.Where(p => p.Current > 0).ToList();
        Assert.NotEmpty(progressIncrements);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithExistingOutputFile_ShouldSkipIfPagesEqual()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            outputPath,
            "existing output",
            TestContext.Current.CancellationToken
        );

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        // Mock metadata handling to return true for pages equal (already upscaled)
        _mockMetadataHandling
            .PagesEqualAsync(inputPath, outputPath)
            .Returns(Task.FromResult(true));

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify that Python service was NOT called since file already exists and pages are equal
        await _mockPythonService
            .DidNotReceive()
            .RunPythonScript(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>()
            );
        await _mockPythonService
            .DidNotReceive()
            .RunPythonScriptStreaming(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Func<string, Task>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>()
            );

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
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Mock metadata handling
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        // Mock Python service to throw on cancellation
        _mockPythonService
            .RunPythonScript(Arg.Any<string>(), Arg.Any<string>(), cts.Token, Arg.Any<TimeSpan?>())
            .Returns(Task.FromCanceled<string>(cts.Token));

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            _upscaler.Upscale(inputPath, outputPath, profile, cts.Token)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithResizing_ShouldCallImageResizeService()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        // Configure max dimension for resizing
        _mockConfig.Value.MaxDimensionBeforeUpscaling = 1024;

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        // Mock metadata handling
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        // Mock image resize service to return a temp file
        string tempResizedPath = Path.Combine(_tempDir, "temp_resized.cbz");
        await File.WriteAllTextAsync(
            tempResizedPath,
            "temp resized content",
            TestContext.Current.CancellationToken
        );

        // Create a real TempResizedCbz instance (but with mock cleanup)
        _mockImageResize
            .CreatePreprocessedTempCbzAsync(
                Arg.Any<string>(),
                Arg.Any<ImagePreprocessingOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                // Use reflection to create the TempResizedCbz since constructor is internal
                ConstructorInfo? constructor = typeof(TempResizedCbz).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(IImageResizeService) },
                    null
                );
                return (TempResizedCbz)
                    constructor!.Invoke(new object[] { tempResizedPath, _mockImageResize });
            });

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify resize service was called with preprocessing options
        await _mockImageResize
            .Received(1)
            .CreatePreprocessedTempCbzAsync(
                inputPath,
                Arg.Is<ImagePreprocessingOptions>(opts =>
                    opts.MaxDimension == 1024 && opts.FormatConversionRules.Count == 0
                ),
                cancellationToken
            );

        // Verify Python service was called
        await _mockPythonService
            .Received(1)
            .RunPythonScript(
                Arg.Any<string>(),
                Arg.Any<string>(),
                cancellationToken,
                Arg.Any<TimeSpan?>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithFormatConversion_ShouldCallImageResizeService()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        // Configure format conversion rules only (no resizing)
        _mockConfig.Value.ImageFormatConversionRules =
        [
            new ImageFormatConversionRule
            {
                FromFormat = ".png",
                ToFormat = ".jpg",
                Quality = 95,
            },
        ];

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        // Mock metadata handling
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        // Mock image resize service to return a temp file
        string tempPreprocessedPath = Path.Combine(_tempDir, "temp_preprocessed.cbz");
        await File.WriteAllTextAsync(
            tempPreprocessedPath,
            "temp preprocessed content",
            TestContext.Current.CancellationToken
        );

        // Create a real TempResizedCbz instance (but with mock cleanup)
        _mockImageResize
            .CreatePreprocessedTempCbzAsync(
                Arg.Any<string>(),
                Arg.Any<ImagePreprocessingOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                // Use reflection to create the TempResizedCbz since constructor is internal
                ConstructorInfo? constructor = typeof(TempResizedCbz).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(IImageResizeService) },
                    null
                );
                return (TempResizedCbz)
                    constructor!.Invoke(new object[] { tempPreprocessedPath, _mockImageResize });
            });

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify preprocessing service was called with format conversion rules
        await _mockImageResize
            .Received(1)
            .CreatePreprocessedTempCbzAsync(
                inputPath,
                Arg.Is<ImagePreprocessingOptions>(opts =>
                    opts.MaxDimension == null
                    && opts.FormatConversionRules.Count == 1
                    && opts.FormatConversionRules[0].FromFormat == ".png"
                    && opts.FormatConversionRules[0].ToFormat == ".jpg"
                    && opts.FormatConversionRules[0].Quality == 95
                ),
                cancellationToken
            );

        // Verify Python service was called
        await _mockPythonService
            .Received(1)
            .RunPythonScript(
                Arg.Any<string>(),
                Arg.Any<string>(),
                cancellationToken,
                Arg.Any<TimeSpan?>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithBothResizingAndFormatConversion_ShouldCallImageResizeServiceWithBothOptions()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        // Configure BOTH max dimension and format conversion rules
        _mockConfig.Value.MaxDimensionBeforeUpscaling = 2048;
        _mockConfig.Value.ImageFormatConversionRules =
        [
            new ImageFormatConversionRule
            {
                FromFormat = ".png",
                ToFormat = ".jpg",
                Quality = 98,
            },
            new ImageFormatConversionRule { FromFormat = ".webp", ToFormat = ".png" },
        ];

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        // Mock metadata handling
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        // Mock image resize service to return a temp file
        string tempPreprocessedPath = Path.Combine(_tempDir, "temp_preprocessed_both.cbz");
        await File.WriteAllTextAsync(
            tempPreprocessedPath,
            "temp preprocessed content",
            TestContext.Current.CancellationToken
        );

        // Create a real TempResizedCbz instance (but with mock cleanup)
        _mockImageResize
            .CreatePreprocessedTempCbzAsync(
                Arg.Any<string>(),
                Arg.Any<ImagePreprocessingOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                // Use reflection to create the TempResizedCbz since constructor is internal
                ConstructorInfo? constructor = typeof(TempResizedCbz).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(IImageResizeService) },
                    null
                );
                return (TempResizedCbz)
                    constructor!.Invoke(new object[] { tempPreprocessedPath, _mockImageResize });
            });

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify preprocessing service was called with BOTH resizing and format conversion options
        await _mockImageResize
            .Received(1)
            .CreatePreprocessedTempCbzAsync(
                inputPath,
                Arg.Is<ImagePreprocessingOptions>(opts =>
                    opts.MaxDimension == 2048
                    && opts.FormatConversionRules.Count == 2
                    && opts.FormatConversionRules[0].FromFormat == ".png"
                    && opts.FormatConversionRules[0].ToFormat == ".jpg"
                    && opts.FormatConversionRules[0].Quality == 98
                    && opts.FormatConversionRules[1].FromFormat == ".webp"
                    && opts.FormatConversionRules[1].ToFormat == ".png"
                ),
                cancellationToken
            );

        // Verify Python service was called with the preprocessed file
        await _mockPythonService
            .Received(1)
            .RunPythonScript(
                Arg.Any<string>(),
                Arg.Any<string>(),
                cancellationToken,
                Arg.Any<TimeSpan?>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WithNoPreprocessing_ShouldNotCallImageResizeService()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        // Ensure no preprocessing is configured
        _mockConfig.Value.MaxDimensionBeforeUpscaling = null;
        _mockConfig.Value.ImageFormatConversionRules = [];

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        // Mock metadata handling
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        // Verify preprocessing service was NOT called
        await _mockImageResize
            .DidNotReceive()
            .CreatePreprocessedTempCbzAsync(
                Arg.Any<string>(),
                Arg.Any<ImagePreprocessingOptions>(),
                Arg.Any<CancellationToken>()
            );

        await _mockImageResize
            .DidNotReceive()
            .CreateResizedTempCbzAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            );

        // Verify Python service was called directly with the original input
        await _mockPythonService
            .Received(1)
            .RunPythonScript(
                Arg.Any<string>(),
                Arg.Any<string>(),
                cancellationToken,
                Arg.Any<TimeSpan?>()
            );
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
            _upscaler.DownloadModelsIfNecessary(cancellationToken)
        );

        // Should either complete successfully or fail with expected exceptions
        Assert.True(
            exception
                is null
                    or FileNotFoundException
                    or InvalidOperationException
                    or HttpRequestException
        );
    }
}
