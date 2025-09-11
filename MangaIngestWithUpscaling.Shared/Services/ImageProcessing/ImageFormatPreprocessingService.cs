using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

[RegisterScoped]
public class ImageFormatPreprocessingService : IImageFormatPreprocessingService
{
    private readonly ILogger<ImageFormatPreprocessingService> _logger;
    private readonly IFileSystem _fileSystem;
    
    private static readonly string[] SupportedImageExtensions = 
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif", ".avif"
    };

    public ImageFormatPreprocessingService(ILogger<ImageFormatPreprocessingService> logger, IFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public async Task<TempPreprocessedCbz> CreatePreprocessedTempCbzAsync(string inputCbzPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputCbzPath))
        {
            throw new FileNotFoundException($"Input CBZ file not found: {inputCbzPath}");
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
            
            _logger.LogInformation("Preprocessing image formats in {InputPath}", inputCbzPath);

            // Extract CBZ to temporary directory
            ZipFile.ExtractToDirectory(inputCbzPath, tempDir);

            // Analyze and preprocess images
            await AnalyzeAndPreprocessImagesInDirectory(tempDir, cancellationToken);

            // Create new CBZ with preprocessed images
            ZipFile.CreateFromDirectory(tempDir, tempCbzPath);
            
            _logger.LogInformation("Created preprocessed temporary CBZ at {TempPath}", tempCbzPath);
            
            return new TempPreprocessedCbz(tempCbzPath, this);
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

    private async Task AnalyzeAndPreprocessImagesInDirectory(string directory, CancellationToken cancellationToken)
    {
        var imageFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (imageFiles.Count == 0)
        {
            _logger.LogInformation("No image files found in directory: {Directory}", directory);
            return;
        }

        _logger.LogDebug("Found {Count} image files to analyze", imageFiles.Count);

        // Analyze image formats to determine the dominant one
        var formatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string imagePath in imageFiles)
        {
            try
            {
                string extension = Path.GetExtension(imagePath).ToLowerInvariant();
                
                // Normalize .jpeg to .jpg for consistency
                if (extension == ".jpeg")
                    extension = ".jpg";

                formatCounts[extension] = formatCounts.GetValueOrDefault(extension, 0) + 1;
                fileFormats[imagePath] = extension;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze image format for: {ImagePath}", imagePath);
                // Continue processing other images
            }
        }

        if (formatCounts.Count == 0)
        {
            _logger.LogWarning("No valid image formats detected");
            return;
        }

        // Determine the dominant format
        var dominantFormat = formatCounts.OrderByDescending(kvp => kvp.Value).First();
        _logger.LogInformation("Dominant image format: {Format} ({Count}/{Total} files)", 
            dominantFormat.Key, dominantFormat.Value, imageFiles.Count);

        // If all images are already the same format, no conversion needed
        if (formatCounts.Count == 1)
        {
            _logger.LogInformation("All images are already in the same format ({Format}), no conversion needed", dominantFormat.Key);
            return;
        }

        // Convert images to the dominant format
        int convertedCount = 0;
        foreach (string imagePath in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            string currentFormat = fileFormats[imagePath];
            if (currentFormat != dominantFormat.Key)
            {
                try
                {
                    await ConvertImageFormat(imagePath, dominantFormat.Key, cancellationToken);
                    convertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to convert image format for: {ImagePath}", imagePath);
                    // Continue processing other images even if one fails
                }
            }
        }

        _logger.LogInformation("Successfully converted {ConvertedCount} images to {TargetFormat}", 
            convertedCount, dominantFormat.Key);
    }

    private async Task ConvertImageFormat(string imagePath, string targetFormat, CancellationToken cancellationToken)
    {
        string originalPath = imagePath;
        string targetPath = Path.ChangeExtension(imagePath, targetFormat);
        
        _logger.LogDebug("Converting image {OriginalPath} to {TargetFormat}", originalPath, targetFormat);

        using var image = await Image.LoadAsync(originalPath, cancellationToken);
        
        // Choose the appropriate encoder based on target format
        IImageEncoder encoder = targetFormat.ToLowerInvariant() switch
        {
            ".jpg" => new JpegEncoder { Quality = 95 }, // High quality to minimize loss
            ".png" => new PngEncoder(),
            ".webp" => new WebpEncoder { Quality = 95 },
            _ => new PngEncoder() // Default to PNG for unknown formats
        };

        // Save to the new format
        await image.SaveAsync(targetPath, encoder, cancellationToken);

        // If the target path is different from original, delete the original file
        if (!string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(originalPath);
            _logger.LogDebug("Converted and replaced {OriginalPath} with {TargetPath}", originalPath, targetPath);
        }
    }
}