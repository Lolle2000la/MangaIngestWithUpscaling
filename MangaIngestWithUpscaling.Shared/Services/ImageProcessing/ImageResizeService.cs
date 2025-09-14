using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Constants;
using Microsoft.Extensions.Logging;
using NetVips;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

[RegisterScoped]
public class ImageResizeService : IImageResizeService
{
    private readonly ILogger<ImageResizeService> _logger;
    private readonly IFileSystem _fileSystem;
    
    private static readonly string[] SupportedImageExtensions = 
        ImageConstants.SupportedImageExtensions.ToArray();

    public ImageResizeService(ILogger<ImageResizeService> logger, IFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public async Task<TempResizedCbz> CreateResizedTempCbzAsync(string inputCbzPath, int maxDimension, CancellationToken cancellationToken)
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
            
            _logger.LogInformation("Resizing images in {InputPath} to max dimension {MaxDimension}", inputCbzPath, maxDimension);

            // Extract CBZ to temporary directory
            ZipFile.ExtractToDirectory(inputCbzPath, tempDir);

            // Process all images in the extracted directory
            await ProcessImagesInDirectory(tempDir, maxDimension, cancellationToken);

            // Create new CBZ with resized images
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

    private async Task ProcessImagesInDirectory(string directory, int maxDimension, CancellationToken cancellationToken)
    {
        var imageFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        _logger.LogDebug("Found {Count} image files to process", imageFiles.Count);

        foreach (string imagePath in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                await Task.Run(() => ResizeImageIfNeeded(imagePath, maxDimension, cancellationToken), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resize image: {ImagePath}", imagePath);
                // Continue processing other images even if one fails
            }
        }
    }

    private void ResizeImageIfNeeded(string imagePath, int maxDimension, CancellationToken cancellationToken)
    {
        // Load image using NetVips
        using var image = Image.NewFromFile(imagePath);
        
        // Check if resizing is needed
        if (image.Width <= maxDimension && image.Height <= maxDimension)
        {
            _logger.LogDebug("Image {ImagePath} ({Width}x{Height}) is already within bounds, skipping resize", 
                imagePath, image.Width, image.Height);
            return;
        }

        // Calculate new dimensions while maintaining aspect ratio
        var (newWidth, newHeight) = CalculateNewDimensions(image.Width, image.Height, maxDimension);
        
        _logger.LogDebug("Resizing image {ImagePath} from {OriginalWidth}x{OriginalHeight} to {NewWidth}x{NewHeight}", 
            imagePath, image.Width, image.Height, newWidth, newHeight);

        // Resize the image
        var resizedImage = image.Resize((double)newWidth / image.Width);
        
        // Save the resized image back to the same path
        resizedImage.WriteToFile(imagePath);
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
}
