using System.Buffers;
using System.IO.Compression;
using System.Numerics;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MathNet.Numerics.IntegralTransforms;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NetVips;

namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

[RegisterScoped]
public class ImageResizeService : IImageResizeService
{
    private static readonly string[] SupportedImageExtensions =
        ImageConstants.SupportedImageExtensions.ToArray();

    /// <summary>
    /// Side length (px) of the square centre crop used for the Laplacian sharpness check and
    /// FFT cliff detection. 512 px balances accuracy against per-image CPU cost.
    /// </summary>
    private const int SmartDownscaleCropSize = 512;

    /// <summary>
    /// A greyscale tile whose mean pixel value (0–255) is above this threshold is considered
    /// "too white" (blank page, speech bubble, margin) and will be skipped when searching for
    /// a content-rich crop region.
    /// </summary>
    private const double CropMaxMean = 250.0;

    /// <summary>
    /// A greyscale tile whose pixel standard deviation is below this threshold lacks enough
    /// contrast to yield a meaningful FFT or Laplacian result and will also be skipped.
    /// </summary>
    private const double CropMinStdDev = 5.0;

    /// <summary>
    /// Cached to avoid a native libvips allocation per image. Intentionally not disposed:
    /// static fields have process lifetime and are not eligible for deterministic disposal.
    /// </summary>
    private static readonly Image LaplacianMask = Image.NewFromArray(
        new int[,]
        {
            { 0, 1, 0 },
            { 1, -4, 1 },
            { 0, 1, 0 },
        }
    );

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

        if (options.EnableSmartDownscale)
        {
            if (options.SmartDownscaleThreshold <= 0)
            {
                throw new ArgumentException(
                    _localizer["Error_SmartDownscaleThresholdMustBePositive"],
                    nameof(options)
                );
            }

            if (options.SmartDownscaleFactor <= 0 || options.SmartDownscaleFactor >= 1)
            {
                throw new ArgumentException(
                    _localizer["Error_SmartDownscaleFactorOutOfRange"],
                    nameof(options)
                );
            }
        }

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

            ZipFile.ExtractToDirectory(inputCbzPath, tempDir);

            await ProcessImagesInDirectory(tempDir, options, cancellationToken);

            ZipFile.CreateFromDirectory(tempDir, tempCbzPath);

            _logger.LogDebug("Created preprocessed temporary CBZ at {TempPath}", tempCbzPath);

            return new TempResizedCbz(tempCbzPath, this);
        }
        finally
        {
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
                    if (ex is OperationCanceledException)
                        throw;
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

        // Fast Laplacian check first; if suspicious, confirm with FFT cliff detection.
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
                double? fftFactor = ComputeFftDownscaleFactor(image, cancellationToken);

                if (fftFactor.HasValue)
                {
                    _logger.LogInformation(
                        "Image {ImagePath} appears cheaply upscaled (sharpness {StdDev:F2} < {Threshold}); "
                            + "FFT cliff detected at {FftPercent:P0} of Nyquist – will downscale by {FftFactor:F3}",
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

        // Apply both max-dimension and smart-downscale constraints in one resize pass so the
        // image is only resampled once (each generation loses quality). We take the more
        // restrictive (smaller) of the two scale factors.
        if (needsResize || needsSmartDownscale)
        {
            double scaleFactor = 1.0;

            if (needsResize)
            {
                var (newWidth, _) = CalculateNewDimensions(
                    image.Width,
                    image.Height,
                    options.MaxDimension!.Value
                );
                scaleFactor = (double)newWidth / image.Width;
            }

            if (needsSmartDownscale)
                scaleFactor = Math.Min(scaleFactor, smartDownscaleFactor);

            int targetWidth = (int)Math.Round(image.Width * scaleFactor);
            int targetHeight = (int)Math.Round(image.Height * scaleFactor);

            _logger.LogDebug(
                "Resizing image {ImagePath} from {OriginalWidth}x{OriginalHeight} to {NewWidth}x{NewHeight} (scale {ScaleFactor:F3})",
                imagePath,
                image.Width,
                image.Height,
                targetWidth,
                targetHeight,
                scaleFactor
            );

            processedImage = image.Resize(scaleFactor, kernel: Enums.Kernel.Linear);
        }

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

            SaveImageWithFormat(
                processedImage,
                newImagePath,
                targetExtension,
                conversionRule.Quality
            );

            if (processedImage != image)
            {
                processedImage.Dispose();
            }

            File.Delete(imagePath);
        }
        else
        {
            processedImage.WriteToFile(imagePath);

            if (processedImage != image)
            {
                processedImage.Dispose();
            }
        }
    }

    /// <summary>
    /// Selects a <see cref="SmartDownscaleCropSize"/>×<see cref="SmartDownscaleCropSize"/>
    /// region of <paramref name="image"/> that contains meaningful ink content (not a blank
    /// margin or speech-bubble background).
    /// <para>
    /// The search tries the centre first, then eight additional positions on a 3×3 grid. The
    /// first candidate whose greyscale mean is ≤ <see cref="CropMaxMean"/> <em>and</em> whose
    /// standard deviation is ≥ <see cref="CropMinStdDev"/> is returned. If no candidate
    /// qualifies the centre crop is returned as a safe fallback.
    /// </para>
    /// </summary>
    /// <returns>
    /// A <see cref="NetVips.Image"/> containing the selected crop that the caller must
    /// dispose. The returned tile may be smaller than
    /// <see cref="SmartDownscaleCropSize"/> near the image boundary.
    /// </returns>
    private static Image FindContentCrop(Image image)
    {
        int imgW = image.Width;
        int imgH = image.Height;
        int tileSize = SmartDownscaleCropSize;

        // Centre is always the first candidate; eight grid positions follow.
        // Grid divides the image into a 3×3 set of anchor points (¼, ½, ¾ in each axis).
        var candidates = new List<(int x, int y)>(9)
        {
            (Math.Max(0, (imgW - tileSize) / 2), Math.Max(0, (imgH - tileSize) / 2)),
        };
        foreach (int qy in new[] { 1, 2, 3 })
        {
            foreach (int qx in new[] { 1, 2, 3 })
            {
                int cx = Math.Max(0, Math.Min(imgW - tileSize, imgW * qx / 4 - tileSize / 2));
                int cy = Math.Max(0, Math.Min(imgH - tileSize, imgH * qy / 4 - tileSize / 2));
                // Avoid an exact duplicate of the centre candidate already added.
                if (cx != candidates[0].x || cy != candidates[0].y)
                    candidates.Add((cx, cy));
            }
        }

        int tileW = Math.Min(tileSize, imgW);
        int tileH = Math.Min(tileSize, imgH);

        Image? fallback = null;
        foreach ((int x, int y) in candidates)
        {
            Image tile = image.ExtractArea(x, y, tileW, tileH);

            using Image tileFlat = tile.HasAlpha() ? tile.Flatten() : tile.Copy();
            using Image tileGrey = tileFlat.Colourspace(Enums.Interpretation.Bw);
            using Image tileUchar = tileGrey.Cast(Enums.BandFormat.Uchar);
            using Image tileStats = tileUchar.Stats();

            double mean = tileStats.Getpoint(4, 0)[0];
            double stdDev = tileStats.Getpoint(5, 0)[0];

            if (mean <= CropMaxMean && stdDev >= CropMinStdDev)
            {
                fallback?.Dispose();
                return tile;
            }

            // Keep the centre crop as the fallback in case nothing better is found.
            if (fallback == null && x == candidates[0].x && y == candidates[0].y)
                fallback = tile;
            else
                tile.Dispose();
        }

        // Nothing was content-rich enough; return the centre crop.
        return fallback ?? image.ExtractArea(candidates[0].x, candidates[0].y, tileW, tileH);
    }

    /// <summary>
    /// Estimates sharpness by computing the standard deviation of the Laplacian over a
    /// center crop of the image. A small Gaussian blur (σ = 0.5) is applied first so that
    /// screentone halftone dots do not inflate the score.
    /// Returns a value ≥ 0; lower means blurrier / more likely to be cheaply upscaled.
    /// </summary>
    private static double ComputeLaplacianStdDev(Image image)
    {
        using Image crop = FindContentCrop(image);

        // Flatten alpha before converting to greyscale so the alpha band does not skew
        // the sharpness score (same pattern as NetVipsPerceptualHash).
        using Image flat = crop.HasAlpha() ? crop.Flatten() : crop.Copy();
        using Image grey = flat.Colourspace(Enums.Interpretation.Bw);

        // A mild Gaussian blur (σ ≈ 0.5) suppresses screentone halftone dots so they don't
        // inflate the sharpness score and trigger false negatives.
        using Image blurred = grey.Gaussblur(0.5);

        using Image laplacian = blurred.Conv(LaplacianMask, precision: Enums.Precision.Float);

        // Column 5 of Stats() is the per-band standard deviation; row 0 is band 0.
        using Image stats = laplacian.Stats();
        double stdDev = stats.Getpoint(5, 0)[0];
        return Math.Abs(stdDev);
    }

    /// <summary>
    /// Uses a forward 2-D FFT on a greyscale centre crop to find the "frequency cliff" that
    /// indicates a cheap (bicubic/bilinear) upscale. When an image has been upscaled, its power
    /// spectrum contains a dead zone above the original Nyquist frequency; native images taper
    /// off gradually all the way to the true Nyquist.
    /// <para>
    /// The FFT is performed entirely in managed code via Math.NET Numerics, so this method works
    /// with any libvips build (including the standard <c>NetVips.Native</c> NuGet packages which
    /// are compiled without FFTW).
    /// </para>
    /// <para>
    /// Returns the inferred scale factor s ∈ (0, 1) – i.e. the ratio of the original resolution
    /// to the current resolution – or <c>null</c> when no clear cliff is detected (native image
    /// or indeterminate). The caller should fall back to the configured
    /// <see cref="ImagePreprocessingOptions.SmartDownscaleFactor"/> in that case.
    /// </para>
    /// </summary>
    private static double? ComputeFftDownscaleFactor(
        Image image,
        CancellationToken cancellationToken
    )
    {
        using Image crop = FindContentCrop(image);

        // Flatten alpha before converting to greyscale so alpha does not produce an extra band
        // that would cause WriteToMemory to return w*h*2 bytes and overflow the Complex array.
        using Image flat = crop.HasAlpha() ? crop.Flatten() : crop.Copy();
        using Image grey = flat.Colourspace(Enums.Interpretation.Bw);

        using Image uchar = grey.Cast(Enums.BandFormat.Uchar);
        byte[] pixels = uchar.WriteToMemory<byte>();
        int w = uchar.Width;
        int h = uchar.Height;

        if (pixels.Length != w * h)
            return null;

        int totalPixels = w * h;
        Complex[] data = ArrayPool<Complex>.Shared.Rent(totalPixels);
        Complex[] rowBuf = ArrayPool<Complex>.Shared.Rent(w);
        Complex[] colBuf = ArrayPool<Complex>.Shared.Rent(h);

        try
        {
            for (int i = 0; i < pixels.Length; i++)
                data[i] = new Complex(pixels[i] / 255.0, 0.0);

            // 2-D FFT as two 1-D passes (Math.NET Numerics only provides 1-D; separable property
            // makes this equivalent). DC component lands at index [0,0] with FourierOptions.Default.
            for (int y = 0; y < h; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Copy(data, y * w, rowBuf, 0, w);
                Fourier.Forward(rowBuf, FourierOptions.Default);
                Array.Copy(rowBuf, 0, data, y * w, w);
            }

            for (int x = 0; x < w; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = 0; y < h; y++)
                    colBuf[y] = data[y * w + x];
                Fourier.Forward(colBuf, FourierOptions.Default);
                for (int y = 0; y < h; y++)
                    data[y * w + x] = colBuf[y];
            }

            // Build a radial power profile (mean magnitude at each integer radius from DC).
            // Pixels in the second half of each axis represent negative frequencies and are
            // mapped back to their positive-frequency equivalent via modular distance.
            int maxRadius = Math.Min(w, h) / 2;
            double[] radialSum = new double[maxRadius + 1];
            int[] radialCount = new int[maxRadius + 1];

            for (int y = 0; y < h; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int dy = y <= h / 2 ? y : h - y;
                for (int x = 0; x < w; x++)
                {
                    int dx = x <= w / 2 ? x : w - x;
                    int r = (int)Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy));
                    if (r <= maxRadius)
                    {
                        radialSum[r] += data[y * w + x].Magnitude;
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

            // ≥ 0.9 Nyquist is treated as native (no clear upscale detected); skip downscale.
            if (scaleFactor >= 0.9)
                return null;

            return Math.Clamp(scaleFactor, 0.25, 0.95);
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(data);
            ArrayPool<Complex>.Shared.Return(rowBuf);
            ArrayPool<Complex>.Shared.Return(colBuf);
        }
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
            int newWidth = maxDimension;
            int newHeight = (int)Math.Round(newWidth / aspectRatio);
            return (newWidth, newHeight);
        }
        else
        {
            int newHeight = maxDimension;
            int newWidth = (int)Math.Round(newHeight * aspectRatio);
            return (newWidth, newHeight);
        }
    }
}
