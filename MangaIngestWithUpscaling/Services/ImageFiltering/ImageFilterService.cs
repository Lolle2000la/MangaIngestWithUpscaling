using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Helpers;
using NetVips;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

[RegisterScoped]
public class ImageFilterService : IImageFilterService
{
    private readonly ILogger<ImageFilterService> _logger;
    private readonly NetVipsPerceptualHash _perceptualHasher;

    public ImageFilterService(ILogger<ImageFilterService> logger)
    {
        _logger = logger;
        // Use NetVips-based perceptual hash implementation
        _perceptualHasher = new NetVipsPerceptualHash();
    }

    public async Task<ImageFilterResult> ApplyFiltersToChapterAsync(string cbzPath, IEnumerable<FilteredImage> filters,
        CancellationToken cancellationToken = default)
    {
        // Delegate to the overload with null upscaled path
        return await ApplyFiltersToChapterAsync(cbzPath, null, filters, cancellationToken);
    }

    public async Task<ImageFilterResult> ApplyFiltersToChapterAsync(string originalCbzPath, string? upscaledCbzPath,
        IEnumerable<FilteredImage> filters, CancellationToken cancellationToken = default)
    {
        var result = new ImageFilterResult();

        if (!File.Exists(originalCbzPath))
        {
            result.ErrorMessages.Add($"Original CBZ file not found: {originalCbzPath}");
            return result;
        }

        if (!string.IsNullOrEmpty(upscaledCbzPath) && !File.Exists(upscaledCbzPath))
        {
            result.ErrorMessages.Add($"Upscaled CBZ file not found: {upscaledCbzPath}");
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

            // First pass: scan for images to filter using the original CBZ (read-only access)
            using (var archive = ZipFile.Open(originalCbzPath, ZipArchiveMode.Read))
            {
                var imageEntries = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Where(e => ImageConstants.IsSupportedImageExtension(Path.GetExtension(e.FullName)))
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
                            entry.FullName, originalCbzPath, imageBytes.Length);

                        // Check if this image matches any filter
                        var matchingFilter = await FindMatchingFilterAsync(imageBytes, entry.FullName, filterList);

                        if (matchingFilter != null)
                        {
                            _logger.LogInformation(
                                "Marking filtered image {ImageName} from {CbzPath} for removal (filter: {FilterDescription})",
                                entry.FullName, originalCbzPath,
                                matchingFilter.Description ?? matchingFilter.OriginalFileName);

                            imagesToRemove.Add(entry.FullName);
                            result.FilteredImageNames.Add(entry.FullName);

                            // Update occurrence count
                            matchingFilter.IncrementOccurrenceCountThreadSafe();
                            matchingFilter.LastMatchedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            _logger.LogDebug("No filter match found for image {ImageName} in {CbzPath}", entry.FullName,
                                originalCbzPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing image {ImageName} in {CbzPath}", entry.FullName,
                            originalCbzPath);
                        result.ErrorMessages.Add($"Error processing {entry.FullName}: {ex.Message}");
                    }
                }
            }

            // Second pass: remove the marked images from both original and upscaled CBZ files
            foreach (var imageToRemove in imagesToRemove)
            {
                // Remove from original CBZ
                try
                {
                    if (CbzCleanupHelpers.TryRemoveImageByName(originalCbzPath, imageToRemove, _logger))
                    {
                        result.FilteredCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing filtered image {ImageName} from original {CbzPath}",
                        imageToRemove, originalCbzPath);
                    result.ErrorMessages.Add($"Error removing {imageToRemove} from original: {ex.Message}");
                }

                // Remove from upscaled CBZ if provided (using base name to handle format changes)
                if (!string.IsNullOrEmpty(upscaledCbzPath))
                {
                    try
                    {
                        if (CbzCleanupHelpers.TryRemoveImageByBaseName(upscaledCbzPath, imageToRemove, _logger))
                        {
                            _logger.LogInformation(
                                "Removed corresponding upscaled image for {ImageName} from {UpscaledCbzPath}",
                                imageToRemove, upscaledCbzPath);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Could not find corresponding upscaled image for {ImageName} in {UpscaledCbzPath}",
                                imageToRemove, upscaledCbzPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error removing filtered image {ImageName} from upscaled {CbzPath}",
                            imageToRemove, upscaledCbzPath);
                        result.ErrorMessages.Add($"Error removing {imageToRemove} from upscaled: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying filters to {CbzPath}", originalCbzPath);
            result.ErrorMessages.Add($"Error accessing CBZ file: {ex.Message}");
        }

        return result;
    }

    public async Task<FilteredImage> CreateFilteredImageFromFileAsync(string imagePath, Library library,
        string? description = null, CancellationToken cancellationToken = default)
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

    public async Task<FilteredImage> CreateFilteredImageFromCbzAsync(string cbzPath, string imageEntryName,
        Library library, string? description = null, CancellationToken cancellationToken = default)
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

        return await CreateFilteredImageFromBytesInternalAsync(imageBytes, entry.FullName, library, mimeType,
            description);
    }

    public Task<string> GenerateThumbnailBase64Async(byte[] imageBytes, int maxSize = 150)
    {
        try
        {
            // Load image using NetVips
            using var image = Image.NewFromBuffer(imageBytes);

            // Calculate new dimensions while maintaining aspect ratio
            var ratio = Math.Min((double)maxSize / image.Width, (double)maxSize / image.Height);
            var newWidth = (int)(image.Width * ratio);
            var newHeight = (int)(image.Height * ratio);

            // Resize the image
            var resizedImage = image.Resize(ratio);

            // Save as JPEG with quality 80
            var jpegBytes = resizedImage.WriteToBuffer(".jpg[Q=80]");

            return Task.FromResult(Convert.ToBase64String(jpegBytes));
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
            // Use NetVips-based implementation that maintains compatibility with existing database hashes
            ulong hash = _perceptualHasher.Hash(imageBytes, _logger);
            return hash; // Returns ulong directly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating perceptual hash");
            throw;
        }
    }

    /// <summary>
    /// Calculates the similarity between two perceptual hashes using NetVips implementation.
    /// Returns a value between 0.0 and 100.0 where 100.0 means identical images.
    /// </summary>
    /// <param name="hash1">First perceptual hash</param>
    /// <param name="hash2">Second perceptual hash</param>
    /// <returns>Similarity percentage (0-100)</returns>
    public double CalculateImageSimilarity(ulong hash1, ulong hash2)
    {
        try
        {
            return NetVipsPerceptualHash.CalculateSimilarity(hash1, hash2);
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
        return NetVipsPerceptualHash.CalculateHammingDistance(hash1, hash2);
    }

    public async Task<FilteredImage> CreateFilteredImageFromBytesAsync(byte[] imageBytes, string fileName,
        Library library, string? mimeType = null, string? description = null)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            mimeType = GetMimeTypeFromExtension(Path.GetExtension(fileName));
        }

        return await CreateFilteredImageFromBytesInternalAsync(imageBytes, fileName, library, mimeType, description);
    }

    private async Task<FilteredImage> CreateFilteredImageFromBytesInternalAsync(byte[] imageBytes, string fileName,
        Library library, string? mimeType, string? description)
    {
        string contentHash = CalculateContentHash(imageBytes);
        ulong perceptualHash = CalculatePerceptualHash(imageBytes);
        string thumbnailBase64 = await GenerateThumbnailBase64Async(imageBytes);

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

    private Task<FilteredImage?> FindMatchingFilterAsync(byte[] imageBytes, string imageName,
        List<FilteredImage> filters)
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
                _logger.LogInformation(
                    "Found perceptual hash match for {ImageName}: similarity={Similarity:F1}%, filter={FilterFileName}",
                    imageName, similarity, filter.OriginalFileName);
                return Task.FromResult<FilteredImage?>(filter);
            }
            else if (similarity > 70.0) // Log near-matches for debugging
            {
                _logger.LogDebug(
                    "Near match for {ImageName}: similarity={Similarity:F1}%, filter={FilterFileName} (threshold: {Threshold}%)",
                    imageName, similarity, filter.OriginalFileName, minSimilarityPercentage);
            }
        }

        _logger.LogDebug(
            "No matching filter found for {ImageName} (content hash: {ContentHash}, perceptual hash: {PerceptualHash})",
            imageName, contentHash, perceptualHash);
        return Task.FromResult<FilteredImage?>(null);
    }

    private static string? GetMimeTypeFromExtension(string extension)
    {
        return ImageConstants.GetMimeTypeFromExtension(extension);
    }
}