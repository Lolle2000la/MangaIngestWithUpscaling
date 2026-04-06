using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NetVips;

namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

[RegisterScoped]
public class ImageResizeService : IImageResizeService
{
    private static readonly string[] SupportedImageExtensions =
        ImageConstants.SupportedImageExtensions.ToArray();

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ImageResizeService> _logger;
    private readonly IStringLocalizer<ImageResizeService> _localizer;

    public ImageResizeService(
        ILogger<ImageResizeService> logger,
        IFileSystem fileSystem,
        IStringLocalizer<ImageResizeService> localizer
    )
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _localizer = localizer;
    }

    public async Task<TempResizedCbz> CreateResizedTempCbzAsync(
        string inputCbzPath,
        int maxDimension,
        CancellationToken cancellationToken
    )
    {
        return await CreatePreprocessedTempCbzAsync(
            inputCbzPath,
            new ImagePreprocessingOptions { MaxDimension = maxDimension },
            cancellationToken
        );
    }

    public async Task<TempResizedCbz> CreatePreprocessedTempCbzAsync(
        string inputCbzPath,
        ImagePreprocessingOptions options,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(inputCbzPath))
        {
            throw new FileNotFoundException(_localizer["Error_InputCbzFileNotFound", inputCbzPath]);
        }

        if (options.MaxDimension.HasValue && options.MaxDimension.Value < 0)
        {
            throw new ArgumentException(
                _localizer["Error_MaxDimensionMustBePositive"],
                nameof(options)
            );
        }

        // Create temporary directory for processing
        string tempDir = Path.Combine(Path.GetTempPath(), $"manga_preprocess_{Guid.NewGuid()}");
        string tempCbzPath = Path.Combine(
            Path.GetTempPath(),
            $"preprocessed_{Guid.NewGuid()}_{Path.GetFileName(inputCbzPath)}"
        );

        try
        {
            Directory.CreateDirectory(tempDir);

            _logger.LogInformation(
                "Preprocessing images in {InputPath} (MaxDimension={MaxDimension}, ConversionRules={RuleCount}, SmartDownscale={SmartDownscale})",
                inputCbzPath,
                options.MaxDimension?.ToString() ?? "none",
                options.FormatConversionRules.Count,
                options.EnableSmartDownscale
            );

            // Extract CBZ to temporary directory
            ZipFile.ExtractToDirectory(inputCbzPath, tempDir);

            // Process all images in the extracted directory
            await ProcessImagesInDirectory(tempDir, options, cancellationToken);

            // Create new CBZ with processed images
            ZipFile.CreateFromDirectory(tempDir, tempCbzPath);

            _logger.LogDebug("Created preprocessed temporary CBZ at {TempPath}", tempCbzPath);

            return new TempResizedCbz(tempCbzPath, this);
        }
        finally
        {
            // Clean up temporary extraction directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    public Task<long> GetMaxPixelCountFromCbzAsync(
        string cbzPath,
        CancellationToken cancellationToken
    )
    {
        return Task.Run(
            () =>
            {
                long maxPixels = 0;

                try
                {
                    using var zipArchive = ZipFile.OpenRead(cbzPath);
                    foreach (var entry in zipArchive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (!SupportedImageExtensions.Contains(ext))
                            continue;

                        try
                        {
                            using var stream = entry.Open();
                            using var image = Image.NewFromStream(stream);
                            long pixels = (long)image.Width * image.Height;
                            if (pixels > maxPixels)
                                maxPixels = pixels;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Failed to read dimensions of image {Entry} in {CbzPath}",
                                entry.Name,
                                cbzPath
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to open or enumerate CBZ archive {CbzPath}",
                        cbzPath
                    );

                    return 0L;
                }
                return maxPixels;
            },
            cancellationToken
        );
    }

    public void CleanupTempFile(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
                _logger.LogDebug("Cleaned up temporary file: {TempFilePath}", tempFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up temporary file: {TempFilePath}",
                tempFilePath
            );
        }
    }

    private async Task ProcessImagesInDirectory(
        string directory,
        ImagePreprocessingOptions options,
        CancellationToken cancellationToken
    )
    {
        var imageFiles = Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        _logger.LogDebug("Found {Count} image files to process", imageFiles.Count);

        await Parallel.ForEachAsync(
            imageFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            },
            (imagePath, ct) =>
            {
                try
                {
                    ProcessImage(imagePath, options, ct);
                    return ValueTask.CompletedTask;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process image: {ImagePath}", imagePath);
                    return ValueTask.CompletedTask; // Continue processing other images even if one fails
                }
            }
        );
    }

    private void ProcessImage(
        string imagePath,
        ImagePreprocessingOptions options,
        CancellationToken cancellationToken
    )
    {
        // Load image using NetVips
        using var image = Image.NewFromFile(imagePath);

        var needsResize =
            options.MaxDimension.HasValue
            && options.MaxDimension.Value > 0
            && (
                image.Width > options.MaxDimension.Value
                || image.Height > options.MaxDimension.Value
            );

        var currentExtension = Path.GetExtension(imagePath).ToLowerInvariant();
        var conversionRule = options.FormatConversionRules.FirstOrDefault(r =>
            r.FromFormat.Equals(currentExtension, StringComparison.OrdinalIgnoreCase)
        );

        var needsFormatConversion = conversionRule != null;

        // Smart downscale: detect cheaply-upscaled images and reduce them before AI upscaling.
        // Primary check (fast): Laplacian variance. If suspicious, fall through to FFT analysis.
        bool needsSmartDownscale = false;
        double smartDownscaleFactor = options.SmartDownscaleFactor;
        if (options.EnableSmartDownscale)
        {
            double sharpness = ComputeLaplacianStdDev(image);
            _logger.LogDebug(
                "Smart downscale check for {ImagePath}: Laplacian std-dev = {StdDev:F2} (threshold {Threshold})",
                imagePath,
                sharpness,
                options.SmartDownscaleThreshold
            );
            if (sharpness < options.SmartDownscaleThreshold)
            {
                // Secondary check (precise): FFT cliff detection to find the exact scale factor.
                double? fftFactor = ComputeFftDownscaleFactor(image);
                if (fftFactor.HasValue)
                {
                    _logger.LogInformation(
                        "Image {ImagePath} appears cheaply upscaled (sharpness {StdDev:F2} < {Threshold}); "
                            + "FFT cliff detected at {FftFactor:P0} of Nyquist – will downscale by {FftFactor:F3}",
                        imagePath,
                        sharpness,
                        options.SmartDownscaleThreshold,
                        fftFactor.Value,
                        fftFactor.Value
                    );
                    smartDownscaleFactor = fftFactor.Value;
                }
                else
                {
                    _logger.LogInformation(
                        "Image {ImagePath} appears cheaply upscaled (sharpness {StdDev:F2} < {Threshold}); "
                            + "FFT found no clear cliff, falling back to configured factor {Factor}",
                        imagePath,
                        sharpness,
                        options.SmartDownscaleThreshold,
                        options.SmartDownscaleFactor
                    );
                }
                needsSmartDownscale = true;
            }
        }

        if (!needsResize && !needsFormatConversion && !needsSmartDownscale)
        {
            _logger.LogDebug(
                "Image {ImagePath} ({Width}x{Height}) needs no preprocessing, skipping",
                imagePath,
                image.Width,
                image.Height
            );
            return;
        }

        Image processedImage = image;

        // Apply resizing if needed
        if (needsResize)
        {
            var (newWidth, newHeight) = CalculateNewDimensions(
                image.Width,
                image.Height,
                options.MaxDimension!.Value
            );

            _logger.LogDebug(
                "Resizing image {ImagePath} from {OriginalWidth}x{OriginalHeight} to {NewWidth}x{NewHeight}",
                imagePath,
                image.Width,
                image.Height,
                newWidth,
                newHeight
            );

            processedImage = image.Resize((double)newWidth / image.Width);
        }

        // Apply smart downscale (after any explicit resize, using Kernel.Cubic for clean results)
        if (needsSmartDownscale)
        {
            Image source = processedImage;
            processedImage = source.Resize(smartDownscaleFactor, kernel: Enums.Kernel.Cubic);
            if (source != image)
                source.Dispose();

            _logger.LogDebug(
                "Smart-downscaled {ImagePath} to {NewWidth}x{NewHeight}",
                imagePath,
                processedImage.Width,
                processedImage.Height
            );
        }

        // Apply format conversion if needed
        if (needsFormatConversion)
        {
            string targetExtension = conversionRule!.ToFormat.ToLowerInvariant();
            if (!targetExtension.StartsWith('.'))
            {
                targetExtension = "." + targetExtension;
            }

            string directory = Path.GetDirectoryName(imagePath)!;
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(imagePath);
            string baseNewImagePath = Path.Combine(
                directory,
                fileNameWithoutExtension + targetExtension
            );
            string newImagePath = baseNewImagePath;
            int suffix = 1;
            while (File.Exists(newImagePath))
            {
                newImagePath = Path.Combine(
                    directory,
                    $"{fileNameWithoutExtension}_{suffix}{targetExtension}"
                );
                suffix++;
            }

            _logger.LogDebug(
                "Converting image {ImagePath} from {FromFormat} to {ToFormat}",
                imagePath,
                currentExtension,
                targetExtension
            );

            // Save the image with the new format
            SaveImageWithFormat(
                processedImage,
                newImagePath,
                targetExtension,
                conversionRule.Quality
            );

            // Delete the original file
            if (processedImage != image)
            {
                processedImage.Dispose();
            }

            File.Delete(imagePath);
        }
        else
        {
            // Save the resized image back to the same path
            processedImage.WriteToFile(imagePath);

            if (processedImage != image)
            {
                processedImage.Dispose();
            }
        }
    }

    /// <summary>
    /// Estimates sharpness by computing the standard deviation of the Laplacian over a 512×512
    /// centre crop of the image. A small Gaussian blur (σ = 0.5) is applied first so that
    /// screentone halftone dots do not inflate the score.
    /// Returns a value ≥ 0; lower means blurrier / more likely to be cheaply upscaled.
    /// </summary>
    private static double ComputeLaplacianStdDev(Image image)
    {
        // Crop a representative area from the centre to keep computation fast.
        const int cropSize = 512;
        int cropX = Math.Max(0, (image.Width - cropSize) / 2);
        int cropY = Math.Max(0, (image.Height - cropSize) / 2);
        int cropW = Math.Min(cropSize, image.Width);
        int cropH = Math.Min(cropSize, image.Height);

        using Image crop = image.ExtractArea(cropX, cropY, cropW, cropH);

        // Convert to single-band (grayscale) so the Laplacian is unambiguous.
        using Image grey = crop.Bands > 1 ? crop.Colourspace(Enums.Interpretation.Bw) : crop.Copy();

        // A mild Gaussian blur (σ ≈ 0.5) suppresses screentone halftone dots so they don't
        // inflate the sharpness score and trigger false negatives.
        using Image blurred = grey.Gaussblur(0.5);

        // Laplacian kernel – highlights edges and fine detail.
        using Image mask = Image.NewFromArray(
            new int[,]
            {
                { 0, 1, 0 },
                { 1, -4, 1 },
                { 0, 1, 0 },
            }
        );
        using Image laplacian = blurred.Conv(mask, precision: Enums.Precision.Float);

        // Stats() returns a 6 × (bands + 1) image.
        // Column indices: 0=min, 1=max, 2=sum, 3=sum², 4=mean, 5=std-dev.
        // Row 0 corresponds to band 0.
        using Image stats = laplacian.Stats();
        double stdDev = stats.Getpoint(5, 0)[0];
        return Math.Abs(stdDev);
    }

    /// <summary>
    /// Uses a forward FFT on a greyscale centre crop to find the "frequency cliff" that indicates
    /// a cheap (bicubic/bilinear) upscale. When an image has been upscaled, its power spectrum
    /// contains a dead zone above the original Nyquist frequency; native images taper off gradually
    /// all the way to the true Nyquist.
    /// <para>
    /// Returns the inferred scale factor s ∈ (0, 1) – i.e. the ratio of the original resolution
    /// to the current resolution – or <c>null</c> when no clear cliff is detected (native image,
    /// indeterminate), or when the underlying libvips build does not include FFT support.
    /// The caller should fall back to the configured
    /// <see cref="ImagePreprocessingOptions.SmartDownscaleFactor"/> in that case.
    /// </para>
    /// </summary>
    private static double? ComputeFftDownscaleFactor(Image image)
    {
        try
        {
            return ComputeFftDownscaleFactorCore(image);
        }
        catch (VipsException)
        {
            // FFT is not available in this libvips build (requires FFTW). Return null so the
            // caller falls back to the configured SmartDownscaleFactor.
            return null;
        }
    }

    private static double? ComputeFftDownscaleFactorCore(Image image)
    {
        const int cropSize = 512;
        int cropX = Math.Max(0, (image.Width - cropSize) / 2);
        int cropY = Math.Max(0, (image.Height - cropSize) / 2);
        int cropW = Math.Min(cropSize, image.Width);
        int cropH = Math.Min(cropSize, image.Height);

        using Image crop = image.ExtractArea(cropX, cropY, cropW, cropH);
        using Image grey = crop.Bands > 1 ? crop.Colourspace(Enums.Interpretation.Bw) : crop.Copy();

        // FFT requires a float input; output is DPCOMPLEX regardless of input precision.
        using Image floatImg = grey.Cast(Enums.BandFormat.Float);

        // Forward FFT: complex output (DPCOMPLEX), DC at (0, 0).
        using Image fft = floatImg.Fwfft();

        // Convert complex to real magnitude (DOUBLE format).
        using Image magnitude = fft.Abs();

        // Write magnitude to a flat buffer for C# analysis.
        // The image is DOUBLE format (8 bytes/pixel), so we read as double[].
        double[] buffer = magnitude.WriteToMemory<double>();
        int w = magnitude.Width;
        int h = magnitude.Height;

        // Build a radial power profile (mean magnitude at each integer radius from DC).
        // Pixels in the second half of each axis represent negative frequencies and are
        // mapped back to their positive-frequency equivalent via modular distance.
        int maxRadius = Math.Min(w, h) / 2;
        double[] radialSum = new double[maxRadius + 1];
        int[] radialCount = new int[maxRadius + 1];

        for (int y = 0; y < h; y++)
        {
            int dy = y <= h / 2 ? y : h - y;
            for (int x = 0; x < w; x++)
            {
                int dx = x <= w / 2 ? x : w - x;
                int r = (int)Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy));
                if (r <= maxRadius)
                {
                    radialSum[r] += buffer[y * w + x];
                    radialCount[r]++;
                }
            }
        }

        // Average magnitude per shell (skip r=0 which is the DC component).
        double[] profile = new double[maxRadius + 1];
        for (int r = 1; r <= maxRadius; r++)
            profile[r] = radialCount[r] > 0 ? radialSum[r] / radialCount[r] : 0.0;

        // The "noise floor" is the average energy in the outermost 10 % of the spectrum.
        int outerStart = (int)(maxRadius * 0.9);
        double noiseFloor = 0.0;
        int outerCount = 0;
        for (int r = outerStart; r <= maxRadius; r++)
        {
            noiseFloor += profile[r];
            outerCount++;
        }
        if (outerCount > 0)
            noiseFloor /= outerCount;

        // Cliff threshold: energy must be significantly above the noise floor.
        // We use 6× the noise floor, clamped to at least 1 % of the non-DC peak.
        double peakNonDC = 0.0;
        for (int r = 1; r <= maxRadius; r++)
            if (profile[r] > peakNonDC)
                peakNonDC = profile[r];

        double cliffThreshold = Math.Max(noiseFloor * 6.0, peakNonDC * 0.01);

        // The cliff is the highest radius where the profile is still above the threshold.
        int cliffRadius = -1;
        for (int r = maxRadius; r >= 1; r--)
        {
            if (profile[r] >= cliffThreshold)
            {
                cliffRadius = r;
                break;
            }
        }

        if (cliffRadius < 0)
            return null;

        double scaleFactor = (double)cliffRadius / maxRadius;

        // Only act if the cliff is meaningfully below Nyquist (i.e. clear upscale detected).
        // A scale factor above 0.9 is treated as "native" and returned as null so the caller
        // skips the downscale.
        if (scaleFactor >= 0.9)
            return null;

        return Math.Clamp(scaleFactor, 0.25, 0.95);
    }

    private void SaveImageWithFormat(
        Image image,
        string outputPath,
        string targetExtension,
        int? quality
    )
    {
        switch (targetExtension)
        {
            case ".jpg":
            case ".jpeg":
                image.Jpegsave(outputPath, q: quality ?? 95);
                break;

            case ".png":
                image.Pngsave(outputPath);
                break;

            case ".webp":
                image.Webpsave(outputPath, q: quality ?? 95);
                break;

            case ".avif":
                image.Heifsave(outputPath, q: quality ?? 95);
                break;

            case ".bmp":
            case ".tiff":
            case ".tif":
            default:
                image.WriteToFile(outputPath);
                break;
        }
    }

    private static (int width, int height) CalculateNewDimensions(
        int originalWidth,
        int originalHeight,
        int maxDimension
    )
    {
        if (originalWidth <= maxDimension && originalHeight <= maxDimension)
        {
            return (originalWidth, originalHeight);
        }

        double aspectRatio = (double)originalWidth / originalHeight;

        if (originalWidth > originalHeight)
        {
            // Width is the limiting dimension
            int newWidth = maxDimension;
            int newHeight = (int)Math.Round(newWidth / aspectRatio);
            return (newWidth, newHeight);
        }
        else
        {
            // Height is the limiting dimension
            int newHeight = maxDimension;
            int newWidth = (int)Math.Round(newHeight * aspectRatio);
            return (newWidth, newHeight);
        }
    }
}
