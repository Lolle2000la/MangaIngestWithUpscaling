using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

[RegisterScoped]
public class ImageResizeService : IImageResizeService
{
    private readonly ILogger<ImageResizeService> _logger;
    private readonly IFileSystem _fileSystem;
    
    private static readonly string[] SupportedImageExtensions = 
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif"
    };

    private static readonly Dictionary<string, IImageFormat> FormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".jpg", JpegFormat.Instance },
        { ".jpeg", JpegFormat.Instance },
        { ".png", PngFormat.Instance },
        { ".webp", WebpFormat.Instance }
    };

    public ImageResizeService(ILogger<ImageResizeService> logger, IFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public async Task<TempResizedCbz> CreateResizedTempCbzAsync(string inputCbzPath, int maxDimension, CancellationToken cancellationToken)
    {
        return await CreateResizedTempCbzAsync(inputCbzPath, maxDimension, true, cancellationToken);
    }

    public async Task<TempResizedCbz> CreateResizedTempCbzAsync(string inputCbzPath, int maxDimension, bool standardizeFormats, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputCbzPath))
        {
            throw new FileNotFoundException($"Input CBZ file not found: {inputCbzPath}");
        }

        if (maxDimension <= 0)
        {
            throw new ArgumentException("Maximum dimension must be greater than 0", nameof(maxDimension));
        }

        // Create temporary directory for processing
        string tempDir = Path.Combine(Path.GetTempPath(), $"manga_resize_{Guid.NewGuid()}");
        string tempCbzPath = Path.Combine(
            Path.GetTempPath(),
            $"resized_{Guid.NewGuid()}_{Path.GetFileName(inputCbzPath)}"
        );

        try
        {
            Directory.CreateDirectory(tempDir);
            
            _logger.LogInformation("Resizing images in {InputPath} to max dimension {MaxDimension} (format standardization: {StandardizeFormats})", 
                inputCbzPath, maxDimension, standardizeFormats);

            // Extract CBZ to temporary directory
            ZipFile.ExtractToDirectory(inputCbzPath, tempDir);

            string? dominantFormat = null;
            if (standardizeFormats)
            {
                // Determine the dominant image format
                dominantFormat = DetermineDominantImageFormat(tempDir);
                _logger.LogInformation("Dominant image format detected: {DominantFormat}", dominantFormat);
            }

            // Process all images in the extracted directory (combined resize and format conversion)
            await ProcessImagesInDirectoryWithOptionalFormatStandardization(tempDir, maxDimension, dominantFormat, cancellationToken);

            // Create new CBZ with processed images
            ZipFile.CreateFromDirectory(tempDir, tempCbzPath);
            
            _logger.LogInformation("Created resized temporary CBZ at {TempPath}", tempCbzPath);
            
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

    public async Task<TempResizedCbz> CreateStandardizedFormatTempCbzAsync(string inputCbzPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputCbzPath))
        {
            throw new FileNotFoundException($"Input CBZ file not found: {inputCbzPath}");
        }

        // Create temporary directory for processing
        string tempDir = Path.Combine(Path.GetTempPath(), $"manga_format_std_{Guid.NewGuid()}");
        string tempCbzPath = Path.Combine(
            Path.GetTempPath(),
            $"standardized_{Guid.NewGuid()}_{Path.GetFileName(inputCbzPath)}"
        );

        try
        {
            Directory.CreateDirectory(tempDir);
            
            _logger.LogInformation("Standardizing image formats in {InputPath}", inputCbzPath);

            // Extract CBZ to temporary directory
            ZipFile.ExtractToDirectory(inputCbzPath, tempDir);

            // Determine the dominant image format
            string dominantFormat = DetermineDominantImageFormat(tempDir);
            _logger.LogInformation("Dominant image format detected: {DominantFormat}", dominantFormat);

            // Standardize image formats to the dominant format
            await StandardizeImageFormats(tempDir, dominantFormat, cancellationToken);

            // Create new CBZ with standardized images
            ZipFile.CreateFromDirectory(tempDir, tempCbzPath);
            
            _logger.LogInformation("Created format-standardized temporary CBZ at {TempPath}", tempCbzPath);
            
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
            _logger.LogWarning(ex, "Failed to clean up temporary file: {TempFilePath}", tempFilePath);
        }
    }

    private async Task ProcessImagesInDirectoryWithOptionalFormatStandardization(string directory, int maxDimension, string? targetFormat, CancellationToken cancellationToken)
    {
        var imageFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        _logger.LogDebug("Found {Count} image files to process", imageFiles.Count);

        IImageFormat? imageFormat = null;
        IImageEncoder? encoder = null;

        if (targetFormat != null)
        {
            if (!FormatMap.TryGetValue(targetFormat, out imageFormat))
            {
                _logger.LogWarning("Unsupported target format {TargetFormat}, defaulting to PNG", targetFormat);
                imageFormat = PngFormat.Instance;
                targetFormat = ".png";
            }

            // Get the appropriate encoder for the target format
            encoder = GetEncoderForFormat(imageFormat);
        }

        int convertedCount = 0;
        foreach (string imagePath in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                bool needsFormatConversion = targetFormat != null && 
                    !string.Equals(Path.GetExtension(imagePath), targetFormat, StringComparison.OrdinalIgnoreCase);
                
                await ResizeAndOptionallyConvertImage(imagePath, maxDimension, targetFormat, encoder, needsFormatConversion, cancellationToken);
                
                if (needsFormatConversion)
                {
                    convertedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process image: {ImagePath}", imagePath);
                // Continue processing other images even if one fails
            }
        }

        if (targetFormat != null && convertedCount > 0)
        {
            _logger.LogInformation("Converted {Count} images to {TargetFormat} during resize operation", convertedCount, targetFormat);
        }
    }

    private async Task ProcessImagesInDirectory(string directory, int maxDimension, CancellationToken cancellationToken)
    {
        await ProcessImagesInDirectoryWithOptionalFormatStandardization(directory, maxDimension, null, cancellationToken);
    }

    private async Task ResizeAndOptionallyConvertImage(string imagePath, int maxDimension, string? targetExtension, IImageEncoder? encoder, bool needsFormatConversion, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(imagePath, cancellationToken);
        
        // Check if resizing is needed
        bool needsResize = image.Width > maxDimension || image.Height > maxDimension;
        
        if (!needsResize && !needsFormatConversion)
        {
            _logger.LogDebug("Image {ImagePath} ({Width}x{Height}) needs no processing, skipping", 
                imagePath, image.Width, image.Height);
            return;
        }

        if (needsResize)
        {
            // Calculate new dimensions while maintaining aspect ratio
            var (newWidth, newHeight) = CalculateNewDimensions(image.Width, image.Height, maxDimension);
            
            _logger.LogDebug("Resizing image {ImagePath} from {OriginalWidth}x{OriginalHeight} to {NewWidth}x{NewHeight}", 
                imagePath, image.Width, image.Height, newWidth, newHeight);

            // Resize the image
            image.Mutate(x => x.Resize(newWidth, newHeight));
        }
        
        // Handle format conversion
        if (needsFormatConversion && targetExtension != null && encoder != null)
        {
            string directory = Path.GetDirectoryName(imagePath)!;
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
            string newPath = Path.Combine(directory, fileNameWithoutExt + targetExtension);

            _logger.LogDebug("Converting image {ImagePath} to {NewPath}", imagePath, newPath);

            // Save in the new format
            await image.SaveAsync(newPath, encoder, cancellationToken);
            
            // Delete the original file if the conversion was successful and it's a different file
            if (!string.Equals(imagePath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(imagePath);
            }
        }
        else
        {
            // Save the resized image back to the same path
            await image.SaveAsync(imagePath, cancellationToken);
        }
    }

    private static IImageEncoder GetEncoderForFormat(IImageFormat format)
    {
        if (format == JpegFormat.Instance)
        {
            return new JpegEncoder();
        }
        else if (format == PngFormat.Instance)
        {
            return new PngEncoder();
        }
        else if (format == WebpFormat.Instance)
        {
            return new WebpEncoder();
        }
        else
        {
            return new PngEncoder(); // Default to PNG
        }
    }

    private static (int width, int height) CalculateNewDimensions(int originalWidth, int originalHeight, int maxDimension)
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

    private string DetermineDominantImageFormat(string directory)
    {
        var imageFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (!imageFiles.Any())
        {
            _logger.LogWarning("No supported image files found in directory {Directory}", directory);
            return ".png"; // Default to PNG
        }

        // Count files by extension
        var formatCounts = imageFiles
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        // Find the most common format
        var dominantFormat = formatCounts.OrderByDescending(kvp => kvp.Value).First().Key;
        
        _logger.LogDebug("Format distribution: {FormatCounts}", string.Join(", ", formatCounts.Select(kvp => $"{kvp.Key}: {kvp.Value}")));
        
        // Check if the dominant format is supported for encoding
        if (!FormatMap.ContainsKey(dominantFormat))
        {
            _logger.LogWarning("Dominant format {DominantFormat} is not supported for encoding, falling back to PNG", dominantFormat);
            return ".png";
        }
        
        return dominantFormat;
    }

    private async Task StandardizeImageFormats(string directory, string targetFormat, CancellationToken cancellationToken)
    {
        var imageFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !string.Equals(Path.GetExtension(f), targetFormat, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!imageFiles.Any())
        {
            _logger.LogDebug("No images need format conversion");
            return;
        }

        _logger.LogInformation("Converting {Count} images to {TargetFormat}", imageFiles.Count, targetFormat);

        if (!FormatMap.TryGetValue(targetFormat, out var imageFormat))
        {
            _logger.LogWarning("Unsupported target format {TargetFormat}, defaulting to PNG", targetFormat);
            imageFormat = PngFormat.Instance;
            targetFormat = ".png";
        }

        foreach (string imagePath in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                await ConvertImageFormat(imagePath, targetFormat, imageFormat, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert image format: {ImagePath}", imagePath);
                // Continue processing other images even if one fails
            }
        }
    }

    private async Task ConvertImageFormat(string imagePath, string targetExtension, IImageFormat targetFormat, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(imagePath)!;
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        string newPath = Path.Combine(directory, fileNameWithoutExt + targetExtension);

        _logger.LogDebug("Converting {ImagePath} to {NewPath}", imagePath, newPath);

        using var image = await Image.LoadAsync(imagePath, cancellationToken);
        
        // Get the appropriate encoder for the target format
        IImageEncoder encoder = GetEncoderForFormat(targetFormat);
        
        // Save in the new format
        await image.SaveAsync(newPath, encoder, cancellationToken);
        
        // Delete the original file if the conversion was successful and it's a different file
        if (!string.Equals(imagePath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(imagePath);
        }
    }
}
