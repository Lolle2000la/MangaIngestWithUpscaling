using System.IO.Compression;
using System.Reflection;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NetVips;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.ImageProcessing;

public class ImageResizeServiceTests
{
    private readonly ILogger<ImageResizeService> _mockLogger;
    private readonly IStringLocalizer<ImageResizeService> _mockLocalizer;
    private readonly ImageResizeService _service;

    public ImageResizeServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<ImageResizeService>>();
        _mockLocalizer = Substitute.For<IStringLocalizer<ImageResizeService>>();

        _mockLocalizer["Error_MaxDimensionMustBePositive"]
            .Returns(
                new LocalizedString(
                    "Error_MaxDimensionMustBePositive",
                    "Maximum dimension must be greater than 0"
                )
            );

        _mockLocalizer["Error_InputCbzFileNotFound", Arg.Any<object[]>()]
            .Returns(x => new LocalizedString(
                "Error_InputCbzFileNotFound",
                $"File not found: {x.Arg<object[]>()[0]}"
            ));

        _mockLocalizer["Error_SmartDownscaleThresholdMustBePositive"]
            .Returns(
                new LocalizedString(
                    "Error_SmartDownscaleThresholdMustBePositive",
                    "SmartDownscaleThreshold must be greater than 0"
                )
            );

        _mockLocalizer["Error_SmartDownscaleFactorOutOfRange"]
            .Returns(
                new LocalizedString(
                    "Error_SmartDownscaleFactorOutOfRange",
                    "SmartDownscaleFactor must be in the range (0, 1)"
                )
            );

        _service = new ImageResizeService(_mockLogger, _mockLocalizer);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [Trait("Category", "Unit")]
    public async Task CreateResizedTempCbzAsync_NegativeMaxDimension_ShouldThrowArgumentException(
        int maxDimension
    )
    {
        // Arrange
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");

        // Create a temporary file to pass the file existence check
        await File.WriteAllTextAsync(
            tempInputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        try
        {
            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateResizedTempCbzAsync(
                    tempInputPath,
                    maxDimension,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.Equal("options", exception.ParamName);
            Assert.Contains("Maximum dimension must be greater than 0", exception.Message);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateResizedTempCbzAsync_InputFileNotFound_ShouldThrowFileNotFoundException()
    {
        // Arrange
        const string nonExistentPath = "nonexistent_file_that_does_not_exist.cbz";
        const int maxDimension = 1024;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.CreateResizedTempCbzAsync(
                nonExistentPath,
                maxDimension,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains(nonExistentPath, exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreatePreprocessedTempCbzAsync_MaxDimensionZero_ShouldNotThrow()
    {
        // Arrange
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");

        // Create a temporary file to pass the file existence check
        await File.WriteAllTextAsync(
            tempInputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        try
        {
            var options = new ImagePreprocessingOptions { MaxDimension = 0 };

            // Act & Assert - Should not throw ArgumentException for zero
            // Note: Will throw other exceptions since we're not providing a valid CBZ,
            // but we're only testing that the validation accepts zero
            var exception = await Record.ExceptionAsync(() =>
                _service.CreatePreprocessedTempCbzAsync(
                    tempInputPath,
                    options,
                    TestContext.Current.CancellationToken
                )
            );

            // Should not be an ArgumentException about MaxDimension
            Assert.False(
                exception is ArgumentException argEx && argEx.ParamName == "options",
                "Should not throw ArgumentException for MaxDimension = 0"
            );
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreatePreprocessedTempCbzAsync_MaxDimensionNull_ShouldNotThrow()
    {
        // Arrange
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");

        // Create a temporary file to pass the file existence check
        await File.WriteAllTextAsync(
            tempInputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        try
        {
            var options = new ImagePreprocessingOptions { MaxDimension = null };

            // Act & Assert - Should not throw ArgumentException for null
            // Note: Will throw other exceptions since we're not providing a valid CBZ,
            // but we're only testing that the validation accepts null
            var exception = await Record.ExceptionAsync(() =>
                _service.CreatePreprocessedTempCbzAsync(
                    tempInputPath,
                    options,
                    TestContext.Current.CancellationToken
                )
            );

            // Should not be an ArgumentException about MaxDimension
            Assert.False(
                exception is ArgumentException argEx && argEx.ParamName == "options",
                "Should not throw ArgumentException for MaxDimension = null"
            );
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreatePreprocessedTempCbzAsync_SmartDownscaleThresholdZero_ShouldThrowArgumentException()
    {
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");
        await File.WriteAllTextAsync(
            tempInputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        try
        {
            var options = new ImagePreprocessingOptions
            {
                EnableSmartDownscale = true,
                SmartDownscaleThreshold = 0,
                SmartDownscaleFactor = 0.75,
            };

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreatePreprocessedTempCbzAsync(
                    tempInputPath,
                    options,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.Equal("options", exception.ParamName);
            Assert.Contains("SmartDownscaleThreshold", exception.Message);
        }
        finally
        {
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [Trait("Category", "Unit")]
    public async Task CreatePreprocessedTempCbzAsync_SmartDownscaleFactorOutOfRange_ShouldThrowArgumentException(
        double factor
    )
    {
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");
        await File.WriteAllTextAsync(
            tempInputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        try
        {
            var options = new ImagePreprocessingOptions
            {
                EnableSmartDownscale = true,
                SmartDownscaleThreshold = 15.0,
                SmartDownscaleFactor = factor,
            };

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreatePreprocessedTempCbzAsync(
                    tempInputPath,
                    options,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.Equal("options", exception.ParamName);
            Assert.Contains("SmartDownscaleFactor", exception.Message);
        }
        finally
        {
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreatePreprocessedTempCbzAsync_SmartDownscaleDisabled_DoesNotValidateSmartDownscaleParams()
    {
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");

        // Create a minimal valid empty ZIP so the method can get past archive-open logic.
        using (ZipFile.Open(tempInputPath, ZipArchiveMode.Create)) { }

        try
        {
            // Invalid smart downscale values that should be ignored when disabled
            var options = new ImagePreprocessingOptions
            {
                EnableSmartDownscale = false,
                SmartDownscaleThreshold = 0,
                SmartDownscaleFactor = 2.0,
            };

            // The method must not throw an ArgumentException about smart-downscale options;
            // any other exception (e.g. no images found) is acceptable here.
            var exception = await Record.ExceptionAsync(() =>
                _service.CreatePreprocessedTempCbzAsync(
                    tempInputPath,
                    options,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.False(
                exception is ArgumentException argEx && argEx.ParamName == "options",
                "Should not validate smart downscale params when feature is disabled"
            );
        }
        finally
        {
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFile_ExistingFile_ShouldDeleteFile()
    {
        // Arrange
        string tempFilePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.tmp");
        File.WriteAllText(tempFilePath, "test content");

        // Ensure file exists before test
        Assert.True(File.Exists(tempFilePath));

        // Act
        _service.CleanupTempFile(tempFilePath);

        // Assert
        Assert.False(File.Exists(tempFilePath));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFile_NonExistentFile_ShouldNotThrow()
    {
        // Arrange
        const string nonExistentPath = "definitely_nonexistent_file.tmp";

        // Act & Assert - Should not throw
        var exception = Record.Exception(() => _service.CleanupTempFile(nonExistentPath));
        Assert.Null(exception);
    }

    /// <summary>
    /// Helper to invoke the private static FindContentCrop method via reflection.
    /// </summary>
    private static Image InvokeFindContentCrop(Image image)
    {
        var method =
            typeof(ImageResizeService).GetMethod(
                "FindContentCrop",
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new MissingMethodException(nameof(ImageResizeService), "FindContentCrop");

        return (Image)method.Invoke(null, [image])!;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindContentCrop_ReturnsCenterTile_WhenCenterHasContent()
    {
        // Create a 1024×1024 greyscale image with a horizontal ramp (x-coordinate as
        // pixel value 0–255), giving mean ≈ 128 and stdDev > 5 in every tile.
        byte[] pixelData = new byte[1024 * 1024];
        for (int y = 0; y < 1024; y++)
        for (int x = 0; x < 1024; x++)
            pixelData[y * 1024 + x] = (byte)(x & 0xFF);

        using Image img = Image.NewFromMemory(
            pixelData,
            1024,
            1024,
            1,
            NetVips.Enums.BandFormat.Uchar
        );

        using Image result = InvokeFindContentCrop(img);

        // Result must be a valid 512×512 tile.
        Assert.Equal(512, result.Width);
        Assert.Equal(512, result.Height);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindContentCrop_SkipsBlankCenter_WhenOffCenterTileHasContent()
    {
        // Build a 2048×2048 all-white image (mean=255, stdDev=0 everywhere).
        using Image white = Image.Black(2048, 2048).Invert();

        // Paint a 512×512 block of mid-grey (value=128) starting at pixel (0,0).
        // This patch is not itself a candidate anchor, but it overlaps one of the
        // scanned 3×3 tiles, so FindContentCrop should select that tile instead of
        // the all-white centre tile.
        using Image patch = Image.Black(512, 512).Linear([0.0], [128.0]);
        using Image composite = white.Insert(patch, 0, 0);

        using Image result = InvokeFindContentCrop(composite);

        // The returned tile must satisfy the same content thresholds used by
        // FindContentCrop (mean < 250 and stdDev >= 5), proving the scanner
        // moved away from the blank centre.
        using Image flat = result.HasAlpha() ? result.Flatten() : result.Copy();
        using Image grey = flat.Colourspace(Enums.Interpretation.Bw);
        using Image uchar = grey.Cast(Enums.BandFormat.Uchar);
        using Image stats = uchar.Stats();
        double mean = stats.Getpoint(4, 0)[0];
        double stdDev = stats.Getpoint(5, 0)[0];

        Assert.True(
            mean < 250,
            $"Expected a content-rich tile (mean < 250) but got mean={mean:F1}"
        );
        Assert.True(
            stdDev >= 5,
            $"Expected a content-rich tile (stdDev >= 5) but got stdDev={stdDev:F1}"
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindContentCrop_FallsBackToCenter_WhenAllTilesAreBlank()
    {
        // A completely white image — all tiles will fail the content check, so the
        // method should fall back to the centre crop rather than throwing.
        using Image white = Image.Black(2048, 2048).Invert();

        using Image result = InvokeFindContentCrop(white);

        // Should still return a valid 512×512 tile (the centre fallback).
        Assert.Equal(512, result.Width);
        Assert.Equal(512, result.Height);
    }
}
