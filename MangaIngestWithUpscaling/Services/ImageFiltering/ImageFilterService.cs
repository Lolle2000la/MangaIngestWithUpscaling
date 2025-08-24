using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Helpers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

[RegisterScoped]
public class ImageFilterService : IImageFilterService
{
    private readonly ILogger<ImageFilterService> _logger;
    private readonly IImageHash _imageHasher;

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif", ".gif"
    };

    public ImageFilterService(ILogger<ImageFilterService> logger)
    {
        _logger = logger;
        // Use pHash algorithm which is excellent for perceptual similarity detection
        _imageHasher = new PerceptualHash();
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
            var imagesToRemove = new List<string>();

            // First pass: scan for images to filter (read-only access)
            using (var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Read))
            {
                var imageEntries = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Where(e => SupportedImageExtensions.Contains(Path.GetExtension(e.FullName)))
                    .ToList();

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

                        _logger.LogDebug("Processing image {ImageName} in {CbzPath} - size: {ImageSize} bytes",
                            entry.FullName, cbzPath, imageBytes.Length);

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
                        else
                        {
                            _logger.LogDebug("No filter match found for image {ImageName} in {CbzPath}", entry.FullName, cbzPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing image {ImageName} in {CbzPath}", entry.FullName, cbzPath);
                        result.ErrorMessages.Add($"Error processing {entry.FullName}: {ex.Message}");
                    }
                }
            }

            // Now remove the marked images using exact filename matching
            foreach (var imageToRemove in imagesToRemove)
            {
                try
                {
                    if (CbzCleanupHelpers.TryRemoveImageByName(cbzPath, imageToRemove, _logger))
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

    /// <summary>
    /// Calculates a perceptual hash using pHash algorithm. This hash is excellent for finding visually similar images
    /// even when they've been resized, recompressed, or slightly modified.
    /// </summary>
    /// <param name="imageBytes">The image data</param>
    /// <returns>Perceptual hash as ulong value</returns>
    public ulong CalculatePerceptualHash(byte[] imageBytes)
    {
        try
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            var hash = _imageHasher.Hash(image);
            return hash; // Returns ulong directly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating perceptual hash");
            throw;
        }
    }

    /// <summary>
    /// Calculates the similarity between two perceptual hashes using CompareHash.
    /// Returns a value between 0.0 and 100.0 where 100.0 means identical images.
    /// </summary>
    /// <param name="hash1">First perceptual hash</param>
    /// <param name="hash2">Second perceptual hash</param>
    /// <returns>Similarity percentage (0-100)</returns>
    public double CalculateImageSimilarity(ulong hash1, ulong hash2)
    {
        try
        {
            return CompareHash.Similarity(hash1, hash2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error comparing perceptual hashes: {Hash1} vs {Hash2}", hash1, hash2);
            return 0.0;
        }
    }

    /// <summary>
    /// Legacy method for backward compatibility. 
    /// Converts similarity percentage to Hamming distance approximation.
    /// </summary>
    /// <param name="hash1">First perceptual hash</param>
    /// <param name="hash2">Second perceptual hash</param>
    /// <returns>Approximate Hamming distance</returns>
    public int CalculateHammingDistance(ulong hash1, ulong hash2)
    {
        var similarity = CalculateImageSimilarity(hash1, hash2);
        // Convert similarity percentage to approximate hamming distance
        // 100% similarity = 0 distance, 0% similarity = 64 distance (for 64-bit hash)
        return (int)Math.Round((100.0 - similarity) * 64.0 / 100.0);
    }

    private async Task<FilteredImage> CreateFilteredImageFromBytesInternalAsync(byte[] imageBytes, string fileName, Library library, string? mimeType, string? description)
    {
        var contentHash = CalculateContentHash(imageBytes);
        var perceptualHash = CalculatePerceptualHash(imageBytes);
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
            PerceptualHash = perceptualHash,
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
        // First try exact content hash matching (fastest)
        var contentHash = CalculateContentHash(imageBytes);
        var exactMatch = filters.FirstOrDefault(f => f.ContentHash == contentHash);
        if (exactMatch != null)
        {
            _logger.LogDebug("Found exact content hash match for {ImageName}: {ContentHash}", imageName, contentHash);
            return Task.FromResult<FilteredImage?>(exactMatch);
        }

        // If no exact match, try perceptual hash matching for visually similar images
        var perceptualHash = CalculatePerceptualHash(imageBytes);

        // For manga images, use a high similarity threshold
        // 90% similarity means very similar images (perfect for manga pages with slight differences)
        const double minSimilarityPercentage = 98.5;

        foreach (var filter in filters.Where(f => f.PerceptualHash.HasValue))
        {
            var similarity = CalculateImageSimilarity(perceptualHash, filter.PerceptualHash!.Value);
            if (similarity >= minSimilarityPercentage)
            {
                _logger.LogInformation("Found perceptual hash match for {ImageName}: similarity={Similarity:F1}%, filter={FilterFileName}",
                    imageName, similarity, filter.OriginalFileName);
                return Task.FromResult<FilteredImage?>(filter);
            }
            else if (similarity > 70.0) // Log near-matches for debugging
            {
                _logger.LogDebug("Near match for {ImageName}: similarity={Similarity:F1}%, filter={FilterFileName} (threshold: {Threshold}%)",
                    imageName, similarity, filter.OriginalFileName, minSimilarityPercentage);
            }
        }

        _logger.LogDebug("No matching filter found for {ImageName} (content hash: {ContentHash}, perceptual hash: {PerceptualHash})",
            imageName, contentHash, perceptualHash);
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
