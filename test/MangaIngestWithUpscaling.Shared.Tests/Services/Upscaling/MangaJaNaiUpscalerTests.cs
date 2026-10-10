using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
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
    private readonly IMangaJaNaiWorkerClient _mockWorkerClient;
    private readonly IOnnxSessionFactory _mockSessionFactory;
    private readonly IStringLocalizer<MangaJaNaiUpscaler> _mockLocalizer;
    private readonly string _tempDir;
    private readonly MangaJaNaiUpscaler _upscaler;

    public MangaJaNaiUpscalerTests()
    {
        _mockWorkerClient = Substitute.For<IMangaJaNaiWorkerClient>();
        _mockSessionFactory = Substitute.For<IOnnxSessionFactory>();
        _mockLogger = Substitute.For<ILogger<MangaJaNaiUpscaler>>();
        _mockFileSystem = Substitute.For<IFileSystem>();
        _mockMetadataHandling = Substitute.For<IMetadataHandlingService>();
        _mockJsonHandling = Substitute.For<IUpscalerJsonHandlingService>();
        _mockImageResize = Substitute.For<IImageResizeService>();
        _mockLocalizer = Substitute.For<IStringLocalizer<MangaJaNaiUpscaler>>();

        _mockLocalizer["Error_InputFileNotFound"]
            .Returns(new LocalizedString("Error_InputFileNotFound", "Input file not found"));
        _mockLocalizer["Error_OutputPathMustBeCbz"]
            .Returns(
                new LocalizedString("Error_OutputPathMustBeCbz", "Output path must be a cbz file")
            );

        if (TestContext.Current.TestCase is null)
        {
            throw new InvalidOperationException(
                "TestContext.Current.TestCase is null. Cannot proceed with test setup."
            );
        }

        // TestCaseDisplayName is not usable in a path: xUnit renders the theory arguments in it and
        // the quotes are an invalid character on Windows. A guid is unique per instance instead,
        // which is all the isolation here needs.
        var config = new UpscalerConfig
        {
            UseFp16 = true,
            UseCPU = false,
            SelectedDeviceIndex = 0,
            RemoteOnly = false,
            PreferredGpuBackend = GpuBackend.Auto,
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"upscaler_test_{Guid.NewGuid():N}"),
            ImageFormatConversionRules = [],
            EnableSmartDownscale = false,
        };
        _mockConfig = Substitute.For<IOptions<UpscalerConfig>>();
        _mockConfig.Value.Returns(config);

        // A separate directory from ModelsDirectory on purpose: the download tests seed model
        // placeholders into this one, and sharing it with the configured models directory would let
        // a test's own files answer for the models it is supposed to be missing.
        _tempDir = Path.Combine(Path.GetTempPath(), $"upscaler_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _upscaler = new MangaJaNaiUpscaler(
            _mockWorkerClient,
            _mockSessionFactory,
            _mockLogger,
            _mockConfig,
            _mockFileSystem,
            _mockMetadataHandling,
            _mockJsonHandling,
            _mockImageResize,
            _mockLocalizer
        );
    }

    public void Dispose()
    {
        foreach (string dir in new[] { _tempDir, _mockConfig.Value.ResolvedModelsDirectory })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // A leftover temp directory is not worth failing a test over.
            }
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

        // Progress<T> posts to the captured SynchronizationContext, so its handler does not
        // necessarily run before Upscale returns. The handler completes the gate on the last report
        // the mock below emits, so the await is a real signal rather than a fixed sleep that either
        // races on a loaded machine or wastes 200 ms on a quiet one.
        var reported = new TaskCompletionSource();
        var progress = new Progress<UpscaleProgress>(p =>
        {
            lock (progressReports)
            {
                progressReports.Add(p);
            }

            if (p is { Total: 5, Current: 3 })
            {
                reported.TrySetResult();
            }
        });

        // Mock the worker client to simulate progress events
        _mockImageResize
            .GetMaxPixelCountFromCbzAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1_000_000L));

        _mockWorkerClient
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>()
            )
            .Returns(call =>
            {
                var workerProgress = call.Arg<IProgress<UpscaleProgress>?>();
                workerProgress?.Report(new UpscaleProgress(5, 0, null, "Reading archive"));
                workerProgress?.Report(new UpscaleProgress(5, 1, null, null));
                workerProgress?.Report(new UpscaleProgress(5, 2, null, null));
                workerProgress?.Report(new UpscaleProgress(5, 3, null, null));
                return Task.FromResult(new UpscaleJobResult("id", "ok", [], 1.0));
            });

        // Mock metadata handling to avoid actual metadata operations
        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, progress, cancellationToken);

        await reported.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.NotEmpty(progressReports);

        // Verify total was set
        UpscaleProgress? totalReport;
        lock (progressReports)
        {
            totalReport = progressReports.FirstOrDefault(p => p.Total.HasValue);
        }
        Assert.NotNull(totalReport);
        Assert.Equal(5, totalReport.Total!.Value);

        // Verify progress increments were reported
        List<UpscaleProgress> progressIncrements;
        lock (progressReports)
        {
            progressIncrements = progressReports.Where(p => p.Current > 0).ToList();
        }
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
        // Verify that the worker client was NOT called since the file already exists and pages are equal
        await _mockWorkerClient
            .DidNotReceive()
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
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

        // Mock worker client to throw on cancellation
        _mockWorkerClient
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
                cts.Token,
                Arg.Any<TimeSpan?>()
            )
            .Returns(Task.FromCanceled<UpscaleJobResult>(cts.Token));

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
                // The constructor is internal, but the assembly is a friend of the shared
                // assembly, so it is callable directly. Constructing it through reflection only
                // renames the constructor at runtime: a rename or signature change would show up
                // as a NullReferenceException instead of a compile error.
                return new TempResizedCbz(tempResizedPath, _mockImageResize);
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

        // Verify the worker was asked to run the job
        await _mockWorkerClient
            .Received(1)
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
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
                // The constructor is internal, but the assembly is a friend of the shared
                // assembly, so it is callable directly. Constructing it through reflection only
                // renames the constructor at runtime: a rename or signature change would show up
                // as a NullReferenceException instead of a compile error.
                return new TempResizedCbz(tempPreprocessedPath, _mockImageResize);
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

        // Verify the worker was asked to run the job
        await _mockWorkerClient
            .Received(1)
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
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
                // The constructor is internal, but the assembly is a friend of the shared
                // assembly, so it is callable directly. Constructing it through reflection only
                // renames the constructor at runtime: a rename or signature change would show up
                // as a NullReferenceException instead of a compile error.
                return new TempResizedCbz(tempPreprocessedPath, _mockImageResize);
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

        // Verify the worker was asked to run the preprocessed file
        await _mockWorkerClient
            .Received(1)
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
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

        // Verify the worker was asked to run the original input directly
        await _mockWorkerClient
            .Received(1)
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
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

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(1_000_000L, 60.0)] // 1 MP → 1× base (1 min)
    [InlineData(2_000_000L, 120.0)] // 2 MP → 2× base (2 min)
    [InlineData(500_000L, 60.0)] // 0.5 MP → clamped to 1× base (1 min)
    [InlineData(5_000_000L, 300.0)] // 5 MP → 5× base (5 min)
    public async Task Upscale_TimeoutScalesWithImagePixelCount(
        long maxPixels,
        double expectedSeconds
    )
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        _mockConfig.Value.UpscaleTimeout = TimeSpan.FromMinutes(1); // base = 1 min per 1 MP

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        _mockImageResize
            .GetMaxPixelCountFromCbzAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(maxPixels));

        TimeSpan? capturedTimeout = null;
        _mockWorkerClient
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Do<TimeSpan?>(t => capturedTimeout = t)
            )
            .Returns(Task.FromResult(new UpscaleJobResult("id", "ok", [], 1.0)));

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        Assert.NotNull(capturedTimeout);
        Assert.Equal(expectedSeconds, capturedTimeout!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upscale_WhenNoImagesInCbz_UsesBaseTimeout()
    {
        // Arrange
        var inputPath = Path.Combine(_tempDir, "input.cbz");
        var outputPath = Path.Combine(_tempDir, "output.cbz");
        await File.WriteAllTextAsync(
            inputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        _mockConfig.Value.UpscaleTimeout = TimeSpan.FromSeconds(45);

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80,
        };

        _mockMetadataHandling
            .PagesEqualAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        // Simulate CBZ with no readable images (returns 0)
        _mockImageResize
            .GetMaxPixelCountFromCbzAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0L));

        TimeSpan? capturedTimeout = null;
        _mockWorkerClient
            .RunJobAsync(
                Arg.Any<UpscaleJobRequest>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Do<TimeSpan?>(t => capturedTimeout = t)
            )
            .Returns(Task.FromResult(new UpscaleJobResult("id", "ok", [], 1.0)));

        var cancellationToken = CancellationToken.None;

        // Act
        await _upscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert – base timeout must be used unchanged
        Assert.NotNull(capturedTimeout);
        Assert.Equal(45.0, capturedTimeout!.Value.TotalSeconds, precision: 1);
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
        if (
            exception != null
            && exception is not FileNotFoundException
            && exception is not InvalidOperationException
        )
        {
            Assert.Fail(
                $"Unexpected exception type: {exception.GetType().Name}, Message: {exception.Message}"
            );
        }

        Assert.True(
            exception
                is null
                    or FileNotFoundException
                    or InvalidOperationException
                    or HttpRequestException
        );
    }

    [Fact]
    [Trait("Category", "Download")]
    [Trait("Category", "Integration")]
    public async Task DownloadModelsIfNecessary_WhenModelMissing_DownloadsAndRestoresModel()
    {
        // Seed every expected model except one target with a placeholder. The download path looks at
        // names and sizes and never at contents, so a placeholder stands in for the real 3.3 GB of
        // models that test_data/ holds — this test used to copy gigabytes across to get the same
        // effect, which is why it could not run on a machine without that directory.
        SeedEveryExpectedModelExcept("2x_IllustrationJaNai_V1_ESRGAN_120k.onnx");

        string missingModelFile = "2x_IllustrationJaNai_V1_ESRGAN_120k.onnx";
        string targetMissingPath = Path.Combine(
            _mockConfig.Value.ResolvedModelsDirectory,
            missingModelFile
        );
        Assert.False(File.Exists(targetMissingPath));

        // Act: Run download if necessary
        await _upscaler.DownloadModelsIfNecessary(CancellationToken.None);

        // Assert: The missing file was downloaded and extracted from GitHub releases
        Assert.True(File.Exists(targetMissingPath));
        Assert.True(new FileInfo(targetMissingPath).Length > 0);
    }

    [Fact]
    [Trait("Category", "Download")]
    [Trait("Category", "Integration")]
    public async Task DownloadModelsIfNecessary_WhenPageBreakDetectorMissing_DownloadsAndRestoresModel()
    {
        SeedEveryExpectedModelExcept("page_break_detector.onnx");

        string targetOnnxPath = Path.Combine(
            _mockConfig.Value.ResolvedModelsDirectory,
            "page_break_detector.onnx"
        );
        Assert.False(File.Exists(targetOnnxPath));

        // Act: Run download
        await _upscaler.DownloadModelsIfNecessary(CancellationToken.None);

        // Assert: The page_break_detector file was downloaded and extracted
        Assert.True(File.Exists(targetOnnxPath));
        Assert.True(new FileInfo(targetOnnxPath).Length > 0);
    }

    [Theory]
    [InlineData(true, "MangaJaNai_V1_FP16_ONNX.zip")]
    [InlineData(false, "MangaJaNai_V1_ONNX.zip")]
    public void GetModelPackages_SelectsAppropriatePackageSuiteBasedOnUseFp16(
        bool? useFp16,
        string expectedMainZip
    )
    {
        var config = new UpscalerConfig { UseFp16 = useFp16, ModelsDirectory = _tempDir };
        var mockConfig = Substitute.For<IOptions<UpscalerConfig>>();
        mockConfig.Value.Returns(config);

        var upscaler = new MangaJaNaiUpscaler(
            _mockWorkerClient,
            _mockSessionFactory,
            _mockLogger,
            mockConfig,
            _mockFileSystem,
            _mockMetadataHandling,
            _mockJsonHandling,
            _mockImageResize,
            _mockLocalizer
        );

        var list = (
            (IEnumerable<MangaJaNaiUpscaler.ModelPackage>)upscaler.GetModelPackages()
        ).ToList();
        Assert.True(list.Count >= 6);

        string? url = list[0].ZipUrl;
        Assert.NotNull(url);
        Assert.Contains(expectedMainZip, url);
    }

    [Fact]
    public void GetModelPackages_WhenUseFp16Null_AndCpuEnabled_SelectsFp32PackageSuite()
    {
        var config = new UpscalerConfig
        {
            UseFp16 = null,
            UseCPU = true,
            ModelsDirectory = _tempDir,
        };
        var mockConfig = Substitute.For<IOptions<UpscalerConfig>>();
        mockConfig.Value.Returns(config);

        var upscaler = new MangaJaNaiUpscaler(
            _mockWorkerClient,
            _mockSessionFactory,
            _mockLogger,
            mockConfig,
            _mockFileSystem,
            _mockMetadataHandling,
            _mockJsonHandling,
            _mockImageResize,
            _mockLocalizer
        );

        var list = (
            (IEnumerable<MangaJaNaiUpscaler.ModelPackage>)upscaler.GetModelPackages()
        ).ToList();
        var firstPackage = list[0];
        var urlProp = firstPackage.GetType().GetProperty("ZipUrl");
        Assert.NotNull(urlProp);

        string? url = urlProp.GetValue(firstPackage) as string;
        Assert.NotNull(url);
        Assert.Contains("MangaJaNai_V1_ONNX.zip", url);
    }

    [Theory]
    [InlineData(GpuBackend.WebGPU, true)]
    [InlineData(GpuBackend.CUDA, false)]
    [InlineData(GpuBackend.DirectML, false)]
    [InlineData(GpuBackend.CPU, false)]
    public void GetModelPackages_OnWebGpu_AlsoDownloadsTheFp32Copies(
        GpuBackend backend,
        bool expectFp32Suite
    )
    {
        // The WebGPU EP returns NaN for the transformer architectures in fp16 and upscales the
        // page black, so the fp32 copies have to be present for the engine to fall back to. Every
        // other provider gets the fp16 set only, which is half the download.
        var config = new UpscalerConfig { UseFp16 = true, ModelsDirectory = _tempDir };
        var mockConfig = Substitute.For<IOptions<UpscalerConfig>>();
        mockConfig.Value.Returns(config);
        _mockSessionFactory.GetEffectiveBackend().Returns(backend);

        var upscaler = new MangaJaNaiUpscaler(
            _mockWorkerClient,
            _mockSessionFactory,
            _mockLogger,
            mockConfig,
            _mockFileSystem,
            _mockMetadataHandling,
            _mockJsonHandling,
            _mockImageResize,
            _mockLocalizer
        );

        var urls = upscaler.GetModelPackages().Select(p => p.ZipUrl).ToList();

        Assert.Contains(urls, u => u.EndsWith("_FP16_ONNX.zip", StringComparison.Ordinal));
        Assert.Equal(
            expectFp32Suite,
            urls.Any(u =>
                u.EndsWith("_ONNX.zip", StringComparison.Ordinal)
                && !u.Contains("_FP16_ONNX", StringComparison.Ordinal)
            )
        );
    }

    [Fact]
    public void ModelPackages_OnlyTheFp32SuiteRenamesItsFiles()
    {
        // The fp16 and fp32 archives ship identical file names: without a suffix on the fp32 side
        // one precision overwrites the other and there is nothing to fall back to.
        Assert.NotEmpty(MangaJaNaiUpscaler.Fp32ModelPackages);
        Assert.NotEmpty(MangaJaNaiUpscaler.Fp16ModelPackages);
        Assert.All(
            MangaJaNaiUpscaler.Fp32ModelPackages,
            p => Assert.Equal(ModelFileNames.Fp32Suffix, p.PrecisionSuffix)
        );
        Assert.All(MangaJaNaiUpscaler.Fp16ModelPackages, p => Assert.Null(p.PrecisionSuffix));
    }

    [Fact]
    public void ExpectedModelFiles_CoversBothPrecisionsUnderDistinctNames()
    {
        // The rule the download path depends on: every archive name resolves to a name on disk, and
        // the two precisions of one family never collide.
        List<string> files = MangaJaNaiUpscaler.ExpectedModelFiles().ToList();

        Assert.NotEmpty(files);
        Assert.Equal(files.Count, files.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx", files);
        Assert.Contains("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx", files);
    }

    /// <summary>
    /// Writes a placeholder for every model the suite expects except <paramref name="missing"/>,
    /// under the name each package stores its files under. With those in place only the package that
    /// holds the missing file is fetched, which is what the test is about; the other packages are
    /// not, and without the placeholders the test would pull down every archive in the release.
    /// </summary>
    private void SeedEveryExpectedModelExcept(string missing) =>
        SeedEveryExpectedModelExcept(missing, MangaJaNaiUpscaler.ExpectedModelFiles());

    private void SeedEveryExpectedModelExcept(string missing, IEnumerable<string> expectedFiles)
    {
        // The configured models directory, not _tempDir: that is the directory the download step
        // looks at, and the two are deliberately not the same.
        string modelsDirectory = _mockConfig.Value.ResolvedModelsDirectory;
        Directory.CreateDirectory(modelsDirectory);

        foreach (string onDisk in expectedFiles)
        {
            // The detector is compared by its archive name, the precision pairs by their stored
            // name, because that is the name the existence check looks for.
            if (
                !string.Equals(onDisk, missing, StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(onDisk).Equals(missing, StringComparison.OrdinalIgnoreCase)
            )
            {
                File.WriteAllText(Path.Combine(modelsDirectory, onDisk), "placeholder");
            }
        }
    }
}
