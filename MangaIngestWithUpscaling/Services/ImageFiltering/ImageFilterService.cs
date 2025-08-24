using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Helpers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

[RegisterScoped]
public class ImageFilterService : IImageFilterService
{
    private readonly ILogger<ImageFilterService> _logger;
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif", ".avif"
    };

    public ImageFilterService(ILogger<ImageFilterService> logger)
    {
        _logger = logger;
    }

    public async Task<ImageFilterResult> ApplyFiltersToChapterAsync(string cbzPath, IEnumerable<FilteredImage> filters, CancellationToken cancellationToken = default)
    {
        var result = new ImageFilterResult();
        
        if (!File.Exists(cbzPath))
        {
            result.ErrorMessages.Add($"CBZ file not found: {cbzPath}");
            return result;
        }

        var filterList = filters.ToList();
        if (!filterList.Any())
        {
            return result; // No filters to apply
        }

        try
        {
            using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Read);
            var imageEntries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Where(e => SupportedImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .ToList();

            var imagesToRemove = new List<string>();

            foreach (var entry in imageEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Read the image data
                    using var entryStream = entry.Open();
                    using var memoryStream = new MemoryStream();
                    await entryStream.CopyToAsync(memoryStream, cancellationToken);
                    var imageBytes = memoryStream.ToArray();

                    // Check if this image matches any filter
                    var matchingFilter = await FindMatchingFilterAsync(imageBytes, entry.FullName, filterList);
                    
                    if (matchingFilter != null)
                    {
                        _logger.LogInformation("Marking filtered image {ImageName} from {CbzPath} for removal (filter: {FilterDescription})", 
                            entry.FullName, cbzPath, matchingFilter.Description ?? matchingFilter.OriginalFileName);
                        
                        imagesToRemove.Add(entry.FullName);
                        result.FilteredImageNames.Add(entry.FullName);
                        
                        // Update occurrence count
                        matchingFilter.OccurrenceCount++;
                        matchingFilter.LastMatchedAt = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing image {ImageName} in {CbzPath}", entry.FullName, cbzPath);
                    result.ErrorMessages.Add($"Error processing {entry.FullName}: {ex.Message}");
                }
            }

            // Now remove the marked images using the helper method
            foreach (var imageToRemove in imagesToRemove)
            {
                try
                {
                    if (CbzCleanupHelpers.TryRemoveImageByBaseName(cbzPath, imageToRemove, _logger))
                    {
                        result.FilteredCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing filtered image {ImageName} from {CbzPath}", imageToRemove, cbzPath);
                    result.ErrorMessages.Add($"Error removing {imageToRemove}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying filters to {CbzPath}", cbzPath);
            result.ErrorMessages.Add($"Error accessing CBZ file: {ex.Message}");
        }

        return result;
    }

    public async Task<FilteredImage> CreateFilteredImageFromFileAsync(string imagePath, Library library, string? description = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath}");
        }

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var fileName = Path.GetFileName(imagePath);
        var mimeType = GetMimeTypeFromExtension(Path.GetExtension(imagePath));

        return await CreateFilteredImageFromBytesInternalAsync(imageBytes, fileName, library, mimeType, description);
    }

    public async Task<FilteredImage> CreateFilteredImageFromCbzAsync(string cbzPath, string imageEntryName, Library library, string? description = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cbzPath))
        {
            throw new FileNotFoundException($"CBZ file not found: {cbzPath}");
        }

        using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Read);
        var entry = archive.GetEntry(imageEntryName);
        
        if (entry == null)
        {
            // Try to find by filename only
            var fileName = Path.GetFileName(imageEntryName);
            entry = archive.Entries.FirstOrDefault(e => Path.GetFileName(e.FullName) == fileName);
        }

        if (entry == null)
        {
            throw new ArgumentException($"Image entry '{imageEntryName}' not found in CBZ file");
        }

        using var entryStream = entry.Open();
        using var memoryStream = new MemoryStream();
        await entryStream.CopyToAsync(memoryStream, cancellationToken);
        var imageBytes = memoryStream.ToArray();

        var mimeType = GetMimeTypeFromExtension(Path.GetExtension(entry.FullName));
        
        return await CreateFilteredImageFromBytesInternalAsync(imageBytes, entry.FullName, library, mimeType, description);
    }

    public async Task<string> GenerateThumbnailBase64Async(byte[] imageBytes, int maxSize = 150)
    {
        try
        {
            using var image = Image.Load(imageBytes);
            
            // Calculate new dimensions while maintaining aspect ratio
            var ratio = Math.Min((double)maxSize / image.Width, (double)maxSize / image.Height);
            var newWidth = (int)(image.Width * ratio);
            var newHeight = (int)(image.Height * ratio);

            image.Mutate(x => x.Resize(newWidth, newHeight));

            using var outputStream = new MemoryStream();
            await image.SaveAsync(outputStream, new JpegEncoder { Quality = 80 });
            
            return Convert.ToBase64String(outputStream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnail");
            throw;
        }
    }

    public string CalculateContentHash(byte[] imageBytes)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(imageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<FilteredImage> CreateFilteredImageFromBytesInternalAsync(byte[] imageBytes, string fileName, Library library, string? mimeType, string? description)
    {
        var contentHash = CalculateContentHash(imageBytes);
        var thumbnailBase64 = await GenerateThumbnailBase64Async(imageBytes);

        return new FilteredImage
        {
            Library = library,
            LibraryId = library.Id,
            OriginalFileName = fileName,
            ThumbnailBase64 = thumbnailBase64,
            MimeType = mimeType,
            FileSizeBytes = imageBytes.Length,
            Description = description,
            ContentHash = contentHash,
            DateAdded = DateTime.UtcNow
        };
    }

    public async Task<FilteredImage> CreateFilteredImageFromBytesAsync(byte[] imageBytes, string fileName, Library library, string? mimeType = null, string? description = null)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            mimeType = GetMimeTypeFromExtension(Path.GetExtension(fileName));
        }
        
        return await CreateFilteredImageFromBytesInternalAsync(imageBytes, fileName, library, mimeType, description);
    }

    private Task<FilteredImage?> FindMatchingFilterAsync(byte[] imageBytes, string imageName, List<FilteredImage> filters)
    {
        // First, try to match by content hash (most accurate)
        var contentHash = CalculateContentHash(imageBytes);
        var hashMatch = filters.FirstOrDefault(f => f.ContentHash == contentHash);
        if (hashMatch != null)
        {
            return Task.FromResult<FilteredImage?>(hashMatch);
        }

        // If no hash match, try to match by filename
        var fileName = Path.GetFileName(imageName);
        var fileNameMatch = filters.FirstOrDefault(f => 
            string.Equals(Path.GetFileName(f.OriginalFileName), fileName, StringComparison.OrdinalIgnoreCase));
        
        if (fileNameMatch != null)
        {
            // Update the content hash for future matches
            fileNameMatch.ContentHash = contentHash;
            return Task.FromResult<FilteredImage?>(fileNameMatch);
        }

        return Task.FromResult<FilteredImage?>(null);
    }

    private static string? GetMimeTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tiff" or ".tif" => "image/tiff",
            ".avif" => "image/avif",
            _ => null
        };
    }
}
