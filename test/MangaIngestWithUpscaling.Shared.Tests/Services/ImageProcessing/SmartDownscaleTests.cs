using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NetVips;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.ImageProcessing;

/// <summary>
/// Integration tests for the hybrid smart-downscale preprocessing step.
/// Tests use synthetic NetVips images so they run without external files.
/// </summary>
public class SmartDownscaleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ImageResizeService _service;

    public SmartDownscaleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"smart_downscale_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        var logger = Substitute.For<ILogger<ImageResizeService>>();
        var localizer = Substitute.For<IStringLocalizer<ImageResizeService>>();

        localizer["Error_MaxDimensionMustBePositive"]
            .Returns(
                new LocalizedString(
                    "Error_MaxDimensionMustBePositive",
                    "Maximum dimension must be greater than 0"
                )
            );
        localizer["Error_InputCbzFileNotFound", Arg.Any<object[]>()]
            .Returns(x => new LocalizedString(
                "Error_InputCbzFileNotFound",
                $"File not found: {x.Arg<object[]>()[0]}"
            ));

        _service = new ImageResizeService(logger, localizer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // -------------------------------------------------------------------------
    // Configuration defaults
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public void ImagePreprocessingOptions_SmartDownscaleDefaults_AreCorrect()
    {
        var options = new ImagePreprocessingOptions();

        Assert.False(options.EnableSmartDownscale);
        Assert.Equal(15.0, options.SmartDownscaleThreshold);
        Assert.Equal(0.75, options.SmartDownscaleFactor);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpscalerConfig_SmartDownscaleDefaults_AreCorrect()
    {
        var config = new UpscalerConfig();

        Assert.False(config.EnableSmartDownscale);
        Assert.Equal(15.0, config.SmartDownscaleThreshold);
        Assert.Equal(0.75, config.SmartDownscaleFactor);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ImagePreprocessingOptions_SmartDownscaleCanBeEnabled()
    {
        var options = new ImagePreprocessingOptions
        {
            EnableSmartDownscale = true,
            SmartDownscaleThreshold = 20.0,
            SmartDownscaleFactor = 0.6,
        };

        Assert.True(options.EnableSmartDownscale);
        Assert.Equal(20.0, options.SmartDownscaleThreshold);
        Assert.Equal(0.6, options.SmartDownscaleFactor);
    }

    // -------------------------------------------------------------------------
    // Integration: sharp image is NOT downscaled
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreprocessedTempCbz_SharpImage_IsNotDownscaled()
    {
        // Arrange: high-contrast alternating-column image → very high Laplacian stddev (~509).
        // With threshold = 1.0 it will never be flagged as cheaply upscaled.
        string cbzPath = CreateCbzWithImage(CreateSharpImage(512, 512));

        var options = new ImagePreprocessingOptions
        {
            EnableSmartDownscale = true,
            SmartDownscaleThreshold = 1.0, // far below the actual score
            SmartDownscaleFactor = 0.5,
        };

        // Act
        using var result = await _service.CreatePreprocessedTempCbzAsync(
            cbzPath,
            options,
            TestContext.Current.CancellationToken
        );

        // Assert: sharp image must pass the check and remain at 512×512.
        var (outW, outH) = ReadFirstImageDimensions(result.FilePath);
        Assert.Equal(512, outW);
        Assert.Equal(512, outH);
    }

    // -------------------------------------------------------------------------
    // Integration: blurry (fake-upscaled) image IS downscaled
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreprocessedTempCbz_BlurryImage_IsDownscaled()
    {
        // Arrange: start with a sharp image then apply heavy Gaussian blur (sigma=10).
        // This produces a Laplacian stddev of ~2, which falls below our threshold of 5.
        string cbzPath = CreateCbzWithImage(CreateBlurredImage(512, 512));

        var options = new ImagePreprocessingOptions
        {
            EnableSmartDownscale = true,
            SmartDownscaleThreshold = 5.0, // blurred stddev ≈ 2.0 < 5.0 → triggers
            SmartDownscaleFactor = 0.5, // fallback if FFT finds no clear cliff
        };

        // Act
        using var result = await _service.CreatePreprocessedTempCbzAsync(
            cbzPath,
            options,
            TestContext.Current.CancellationToken
        );

        // Assert: output must be smaller than 512 in both dimensions (downscaled).
        var (outW, outH) = ReadFirstImageDimensions(result.FilePath);
        Assert.True(outW < 512, $"Expected width < 512 but got {outW}");
        Assert.True(outH < 512, $"Expected height < 512 but got {outH}");
    }

    // -------------------------------------------------------------------------
    // Integration: content-aware crop selection skips blank centre
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that <c>FindContentCrop</c> skips a blank/white centre tile and selects
    /// an off-centre tile with real content. The test image is 1600×1600 so that the
    /// centre crop (at offset 544,544) is entirely white, while the top-left 512×512
    /// quadrant contains a sharp alternating pattern that satisfies the content criteria
    /// (mean ≤ 250, std-dev ≥ 5). Because the selected tile is sharp, ComputeLaplacianStdDev
    /// returns a high score and the image is <em>not</em> downscaled.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreprocessedTempCbz_WhiteCentreWithSharpCorner_IsNotDownscaled()
    {
        // Arrange: 1600×1600 image – white everywhere except the top-left 512×512 which has
        // alternating columns (very sharp). The centre tile (offset 544,544) is all white so
        // FindContentCrop must skip it and use an off-centre candidate that contains the sharp content.
        string cbzPath = CreateCbzWithImage(CreateImageWithWhiteCentreAndSharpCorner(1600, 1600));

        var options = new ImagePreprocessingOptions
        {
            EnableSmartDownscale = true,
            SmartDownscaleThreshold = 5.0, // the sharp tile has a Laplacian stddev >> 5
            SmartDownscaleFactor = 0.5,
        };

        // Act
        using var result = await _service.CreatePreprocessedTempCbzAsync(
            cbzPath,
            options,
            TestContext.Current.CancellationToken
        );

        // Assert: the sharp tile must be selected, so the image is NOT downscaled.
        var (outW, outH) = ReadFirstImageDimensions(result.FilePath);
        Assert.Equal(1600, outW);
        Assert.Equal(1600, outH);
    }

    // -------------------------------------------------------------------------
    // Integration: smart downscale disabled – blurry image is left untouched
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreprocessedTempCbz_SmartDownscaleDisabled_BlurryImageIsUnchanged()
    {
        string cbzPath = CreateCbzWithImage(CreateBlurredImage(512, 512));

        var options = new ImagePreprocessingOptions
        {
            EnableSmartDownscale = false,
            SmartDownscaleThreshold = 5.0,
            SmartDownscaleFactor = 0.5,
        };

        // Act
        using var result = await _service.CreatePreprocessedTempCbzAsync(
            cbzPath,
            options,
            TestContext.Current.CancellationToken
        );

        var (outW, outH) = ReadFirstImageDimensions(result.FilePath);
        Assert.Equal(512, outW);
        Assert.Equal(512, outH);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string CreateCbzWithImage(byte[] jpegBytes)
    {
        string cbzPath = Path.Combine(_tempDir, $"{Guid.NewGuid()}.cbz");
        using var zip = ZipFile.Open(cbzPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("page001.jpg");
        using var stream = entry.Open();
        stream.Write(jpegBytes);
        return cbzPath;
    }

    /// <summary>
    /// Creates a JPEG where the centre <c>512×512</c> tile is pure white and the top-left
    /// <c>512×512</c> corner contains sharp alternating black/white columns.
    /// For <paramref name="width"/> and <paramref name="height"/> &gt; 1536 the two regions do
    /// not overlap, so <c>FindContentCrop</c> must skip the blank centre and return the corner.
    /// </summary>
    private static byte[] CreateImageWithWhiteCentreAndSharpCorner(int width, int height)
    {
        // Fill entire image with white.
        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);

        // Paint alternating columns in the top-left 512×512 tile.
        int tileSize = 512;
        for (int y = 0; y < Math.Min(tileSize, height); y++)
        {
            for (int x = 0; x < Math.Min(tileSize, width); x++)
            {
                pixels[y * width + x] = (byte)(x % 2 == 0 ? 255 : 0);
            }
        }

        using var image = Image.NewFromMemory(pixels, width, height, 1, Enums.BandFormat.Uchar);
        return image.JpegsaveBuffer(q: 95);
    }

    /// <summary>Creates a JPEG of alternating black/white pixel columns (very sharp).</summary>
    private static byte[] CreateSharpImage(int width, int height)
    {
        byte[] pixels = Enumerable
            .Range(0, width * height)
            .Select(i => (byte)((i % width % 2 == 0) ? 255 : 0))
            .ToArray();

        using var image = Image.NewFromMemory(pixels, width, height, 1, Enums.BandFormat.Uchar);
        return image.JpegsaveBuffer(q: 95);
    }

    /// <summary>
    /// Creates a JPEG that simulates a cheap bicubic upscale: start with sharp alternating
    /// columns then apply a heavy Gaussian blur (σ = 10). The resulting Laplacian stddev is
    /// around 2, well below the default threshold of 15.
    /// </summary>
    private static byte[] CreateBlurredImage(int width, int height)
    {
        byte[] pixels = Enumerable
            .Range(0, width * height)
            .Select(i => (byte)((i % width % 2 == 0) ? 255 : 0))
            .ToArray();

        using var sharp = Image.NewFromMemory(pixels, width, height, 1, Enums.BandFormat.Uchar);
        using var blurred = sharp.Gaussblur(10.0);
        return blurred.JpegsaveBuffer(q: 95);
    }

    private static (int width, int height) ReadFirstImageDimensions(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        var entry = zip.Entries.First(e =>
            e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        );
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        using var img = Image.NewFromBuffer(ms.ToArray());
        return (img.Width, img.Height);
    }
}
