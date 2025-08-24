using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

/// <summary>
/// Service to help migrate existing filtered images to include perceptual hashes
/// </summary>
[RegisterScoped]
public class PerceptualHashMigrationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IImageFilterService _imageFilterService;
    private readonly ILogger<PerceptualHashMigrationService> _logger;

    public PerceptualHashMigrationService(
        ApplicationDbContext dbContext,
        IImageFilterService imageFilterService,
        ILogger<PerceptualHashMigrationService> logger)
    {
        _dbContext = dbContext;
        _imageFilterService = imageFilterService;
        _logger = logger;
    }

    /// <summary>
    /// Updates existing filtered images that don't have perceptual hashes
    /// This method reconstructs the perceptual hash from the thumbnail image
    /// </summary>
    public async Task UpdateExistingFilteredImagesAsync(CancellationToken cancellationToken = default)
    {
        var filteredImagesWithoutPerceptualHash = await _dbContext.FilteredImages
            .Where(f => !f.PerceptualHash.HasValue && !string.IsNullOrEmpty(f.ThumbnailBase64))
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} filtered images without perceptual hashes", filteredImagesWithoutPerceptualHash.Count);

        var updated = 0;
        foreach (var filteredImage in filteredImagesWithoutPerceptualHash)
        {
            try
            {
                if (string.IsNullOrEmpty(filteredImage.ThumbnailBase64))
                    continue;

                // Convert base64 thumbnail back to bytes
                var thumbnailBytes = Convert.FromBase64String(filteredImage.ThumbnailBase64);

                // Calculate perceptual hash from thumbnail
                // Note: This won't be as accurate as the original image, but it's better than nothing
                var perceptualHash = _imageFilterService.CalculatePerceptualHash(thumbnailBytes);

                filteredImage.PerceptualHash = perceptualHash;
                updated++;

                _logger.LogDebug("Updated perceptual hash for {FileName}: {PerceptualHash}",
                    filteredImage.OriginalFileName, perceptualHash);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate perceptual hash for {FileName}",
                    filteredImage.OriginalFileName);
            }
        }

        if (updated > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated {UpdatedCount} filtered images with perceptual hashes", updated);
        }
    }
}
