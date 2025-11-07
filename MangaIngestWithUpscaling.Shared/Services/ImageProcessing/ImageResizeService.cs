using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
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

    public ImageResizeService(ILogger<ImageResizeService> logger, IFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
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
            throw new FileNotFoundException($"Input CBZ file not found: {inputCbzPath}");
        }

        if (options.MaxDimension.HasValue && options.MaxDimension.Value <= 0)
        {
            throw new ArgumentException(
                "Maximum dimension must be greater than 0",
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
                "Preprocessing images in {InputPath} (MaxDimension={MaxDimension}, ConversionRules={RuleCount})",
                inputCbzPath,
                options.MaxDimension?.ToString() ?? "none",
                options.FormatConversionRules.Count
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
            && (
                image.Width > options.MaxDimension.Value
                || image.Height > options.MaxDimension.Value
            );

        var currentExtension = Path.GetExtension(imagePath).ToLowerInvariant();
        var conversionRule = options.FormatConversionRules.FirstOrDefault(r =>
            r.FromFormat.Equals(currentExtension, StringComparison.OrdinalIgnoreCase)
        );

        var needsFormatConversion = conversionRule != null;

        if (!needsResize && !needsFormatConversion)
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
            string newImagePath = Path.Combine(
                directory,
                fileNameWithoutExtension + targetExtension
            );

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
