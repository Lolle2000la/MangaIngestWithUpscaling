using System.IO.Compression;
using System.Reflection;
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

    [Fact]
    [Trait("Category", "Unit")]
    public void VerifyReady_ExercisesResizeAndRealPngJpegCodecs()
    {
        // Uses the native libvips PNG/JPEG codecs, which the rest of this suite already depends on. A
        // libvips built without them (or with a broken loader) must throw here rather than at the first
        // page.
        _service.VerifyReady();
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_ConversionKeepsTheConvertedExtension()
    {
        // A .png input whose rule targets .jpg must come out as page.jpg and report that path, exactly
        // as the whole-CBZ path keeps the converted entry name. Renaming the converted bytes back over
        // page.png would leave the file's name and content disagreeing.
        string directory = Directory.CreateTempSubdirectory("preprocess_page").FullName;
        try
        {
            string path = Path.Combine(directory, "page.png");
            using (Image image = Image.Black(8, 8))
            {
                image.WriteToFile(path);
            }

            string result = await _service.PreprocessImageInPlaceAsync(
                path,
                new ImagePreprocessingOptions
                {
                    EnableSmartDownscale = false,
                    FormatConversionRules =
                    [
                        new MangaIngestWithUpscaling.Shared.Configuration.ImageFormatConversionRule
                        {
                            FromFormat = ".png",
                            ToFormat = ".jpg",
                            Quality = 90,
                        },
                    ],
                },
                TestContext.Current.CancellationToken
            );

            // The returned path keeps the converted extension and the original file is replaced.
            Assert.Equal(Path.Combine(directory, "page.jpg"), result);
            Assert.False(
                File.Exists(path),
                "the original .png must be replaced by the converted page"
            );
            Assert.True(File.Exists(result));

            // libvips sniffs the content; a JPEG starts with the SOI marker 0xFF 0xD8.
            byte[] bytes = File.ReadAllBytes(result);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xD8, bytes[1]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_ConversionFromPngToJpgKeepsTheColourUnderTransparency()
    {
        // The default conversion rules turn a PNG (or AVIF) page into a JPG. JPEG cannot store an
        // alpha band, and Jpegsave composites an RGBA image onto black, so a transparent region used
        // to be black-baked. The reference (and the engine and detector) instead keep the colour
        // stored under a transparent pixel and discard the band; the left half here is red at
        // alpha=0, the right half opaque blue, so a black-composite is distinguishable from the
        // stored colour.
        string directory = Directory.CreateTempSubdirectory("preprocess_alpha").FullName;
        try
        {
            string path = Path.Combine(directory, "page.png");
            using (Image image = CreateRedBlueRgba(8, 8))
            {
                image.WriteToFile(path);
            }

            string result = await _service.PreprocessImageInPlaceAsync(
                path,
                new ImagePreprocessingOptions
                {
                    EnableSmartDownscale = false,
                    FormatConversionRules =
                    [
                        new MangaIngestWithUpscaling.Shared.Configuration.ImageFormatConversionRule
                        {
                            FromFormat = ".png",
                            ToFormat = ".jpg",
                            Quality = 95,
                        },
                    ],
                },
                TestContext.Current.CancellationToken
            );

            Assert.Equal(Path.Combine(directory, "page.jpg"), result);
            using Image jpeg = Image.NewFromFile(result);
            Assert.Equal(3, jpeg.Bands);

            // The transparent pixel keeps its stored red; a black-composite would have written [0,0,0].
            byte[] transparent = ReadPixel(jpeg, 0, 0);
            Assert.True(
                transparent[0] > 220,
                $"the colour under the transparent pixel must survive as red, but was [{transparent[0]},{transparent[1]},{transparent[2]}]"
            );
            Assert.True(
                transparent[1] < 40 && transparent[2] < 40,
                $"the transparent pixel must not be black-composited, but was [{transparent[0]},{transparent[1]},{transparent[2]}]"
            );

            // The opaque half is untouched.
            byte[] opaque = ReadPixel(jpeg, 4, 0);
            Assert.True(
                opaque[2] > 220 && opaque[0] < 40,
                $"the opaque pixel must stay blue, but was [{opaque[0]},{opaque[1]},{opaque[2]}]"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_ConversionToAlphaCapableFormatPreservesAlpha()
    {
        // PNG, WebP, AVIF, TIFF and BMP can all store an alpha band, so the conversion must keep it
        // rather than dropping it: only a target that cannot carry alpha should discard the band.
        string directory = Directory.CreateTempSubdirectory("preprocess_alpha_webp").FullName;
        try
        {
            string path = Path.Combine(directory, "page.png");
            using (Image image = CreateRedBlueRgba(8, 8))
            {
                image.WriteToFile(path);
            }

            string result = await _service.PreprocessImageInPlaceAsync(
                path,
                new ImagePreprocessingOptions
                {
                    EnableSmartDownscale = false,
                    FormatConversionRules =
                    [
                        new MangaIngestWithUpscaling.Shared.Configuration.ImageFormatConversionRule
                        {
                            FromFormat = ".png",
                            ToFormat = ".webp",
                            Quality = 95,
                        },
                    ],
                },
                TestContext.Current.CancellationToken
            );

            Assert.Equal(Path.Combine(directory, "page.webp"), result);

            byte[] bytes = File.ReadAllBytes(result);
            // RIFF....WEBP is the WebP container signature.
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));

            using Image webp = Image.NewFromFile(result);
            Assert.SkipWhen(
                !webp.HasAlpha(),
                "this libvips build's WebP encoder does not carry an alpha band"
            );

            Assert.Equal(4, webp.Bands);
            byte[] transparent = ReadPixel(webp, 0, 0);
            Assert.Equal(0, transparent[3]);
            // The stored red is kept too: dropping the band would have discarded both the alpha and,
            // on a target that keeps it, changed nothing here — the point is the band is still there.
            Assert.True(
                transparent[0] > 180,
                $"the colour under the transparent pixel must be kept, but was [{transparent[0]},{transparent[1]},{transparent[2]}]"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Reads the raw band bytes of the pixel at (<paramref name="x"/>, <paramref name="y"/>) from an
    /// interleaved 8-bit image.
    /// </summary>
    private static byte[] ReadPixel(Image image, int x, int y)
    {
        byte[] bytes = image.WriteToMemory<byte>();
        int bands = image.Bands;
        int offset = (y * image.Width + x) * bands;
        return bytes[offset..(offset + bands)];
    }

    /// <summary>
    /// Builds an 8-bit sRGB image with an alpha band whose left half is fully transparent red and
    /// whose right half is opaque blue. The transparent half deliberately stores a colour the
    /// black-composite of <c>Flatten()</c> would destroy, so the two choices are distinguishable.
    /// </summary>
    private static Image CreateRedBlueRgba(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * width + x;
            int i = p * 3;
            if (x < width / 2)
            {
                rgb[i] = 255; // red
                alpha[p] = 0; // transparent
            }
            else
            {
                rgb[i + 2] = 255; // blue
                alpha[p] = 255;
            }
        }

        using Image rgbImage = Image.NewFromMemory(rgb, width, height, 3, Enums.BandFormat.Uchar);
        using Image alphaImage = Image.NewFromMemory(
            alpha,
            width,
            height,
            1,
            Enums.BandFormat.Uchar
        );
        // Bandjoin yields Multiband; make the sRGB interpretation explicit so HasAlpha() is true and
        // the save path recognises the alpha band, exactly as a decoded PNG/AVIF does.
        return rgbImage.Bandjoin(alphaImage).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_ResizeKeepsTheOriginalName()
    {
        // A resize changes no extension, so the page stays at its original path and the returned path
        // is the input path.
        string directory = Directory.CreateTempSubdirectory("preprocess_page_resize").FullName;
        try
        {
            string path = Path.Combine(directory, "page.png");
            using (Image image = Image.Black(64, 64))
            {
                image.WriteToFile(path);
            }

            string result = await _service.PreprocessImageInPlaceAsync(
                path,
                new ImagePreprocessingOptions { EnableSmartDownscale = false, MaxDimension = 32 },
                TestContext.Current.CancellationToken
            );

            Assert.Equal(path, result);
            using Image resized = Image.NewFromFile(path);
            Assert.Equal(32, resized.Width);
            Assert.Equal(32, resized.Height);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_ResizeOnlyJpeg_SavesAtExplicitQualityNotLibvipsDefault()
    {
        // A resize-only pass on a JPEG must re-encode through the explicit-quality save path. Falling
        // through to WriteToFile lets libvips pick jpegsave's default Q≈75, so the resized page loses
        // quality before it is upscaled. The saved bytes are compared with an explicit Q95 encode of
        // the same resize and shown to differ from a default encode, so the assertion pins the
        // quality rather than merely "some bytes were written".
        string directory = Directory.CreateTempSubdirectory("preprocess_jpeg_quality").FullName;
        try
        {
            // High-frequency noise makes the Q75/Q95 reference encodes differ substantially.
            byte[] noise = new byte[128 * 128];
            new Random(1234).NextBytes(noise);

            byte[] sourceJpeg;
            using (Image original = Image.NewFromMemory(noise, 128, 128, 1, Enums.BandFormat.Uchar))
            {
                sourceJpeg = original.JpegsaveBuffer(q: 95);
            }

            string path = Path.Combine(directory, "page.jpg");
            await File.WriteAllBytesAsync(path, sourceJpeg, TestContext.Current.CancellationToken);

            string result = await _service.PreprocessImageInPlaceAsync(
                path,
                new ImagePreprocessingOptions { EnableSmartDownscale = false, MaxDimension = 64 },
                TestContext.Current.CancellationToken
            );

            Assert.Equal(path, result);
            byte[] actual = await File.ReadAllBytesAsync(
                result,
                TestContext.Current.CancellationToken
            );

            // Reproduce the service's transform (decode -> linear resize to 64 px) and encode the same
            // resized image both at the explicit Q95 the pipeline uses and at libvips' default.
            byte[] expectedQ95;
            byte[] defaultEncoded;
            using (Image source = Image.NewFromBuffer(sourceJpeg))
            {
                double scaleFactor = 64.0 / source.Width;
                using Image resized = source.Resize(scaleFactor, kernel: Enums.Kernel.Linear);
                expectedQ95 = resized.WriteToBuffer(".jpg[Q=95]");
                defaultEncoded = resized.WriteToBuffer(".jpg");
            }

            // Sanity: the two reference encodes really differ, so the equality below is meaningful.
            Assert.NotEqual(defaultEncoded.Length, expectedQ95.Length);
            Assert.Equal(expectedQ95, actual);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_InvalidSmartDownscale_ShouldThrow()
    {
        // The streamed in-place path must validate like the whole-CBZ path, or a bad smart-downscale
        // configuration is silently swallowed per image and the page is upscaled without the
        // downscale the engine identity claims.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.PreprocessImageInPlaceAsync(
                "unused.png",
                new ImagePreprocessingOptions
                {
                    EnableSmartDownscale = true,
                    SmartDownscaleThreshold = 1.0,
                    SmartDownscaleFactor = 0,
                },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PreprocessImageInPlaceAsync_KeepsTheOriginalWhenTheImageCannotBeDecoded()
    {
        // A corrupt page must be copied through (the whole-CBZ path tolerated a bad image) rather than
        // failing the whole chapter, and the original must be left untouched.
        string imagePath = Path.Combine(Path.GetTempPath(), $"corrupt_{Guid.NewGuid():N}.png");
        byte[] original = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(imagePath, original, TestContext.Current.CancellationToken);
        try
        {
            string result = await _service.PreprocessImageInPlaceAsync(
                imagePath,
                new ImagePreprocessingOptions { MaxDimension = 100, FormatConversionRules = [] },
                TestContext.Current.CancellationToken
            );

            // Nothing was preprocessed, so the original is kept and its path is reported back.
            Assert.Equal(imagePath, result);
            Assert.Equal(
                original,
                await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}
