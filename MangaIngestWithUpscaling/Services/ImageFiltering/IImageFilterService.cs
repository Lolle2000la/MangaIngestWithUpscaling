using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

/// <summary>
/// Result of applying image filters to a chapter
/// </summary>
public class ImageFilterResult
{
    public int FilteredCount { get; set; }
    public List<string> FilteredImageNames { get; set; } = [];
    public List<string> ErrorMessages { get; set; } = [];
}

/// <summary>
/// Service for applying image filters to CBZ chapters
/// </summary>
public interface IImageFilterService
{
    /// <summary>
    /// Applies the specified image filters to a CBZ chapter file
    /// </summary>
    /// <param name="cbzPath">Path to the CBZ file</param>
    /// <param name="filters">List of filtered images to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing information about filtered images</returns>
    Task<ImageFilterResult> ApplyFiltersToChapterAsync(string cbzPath, IEnumerable<FilteredImage> filters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the specified image filters to both original and upscaled CBZ chapter files.
    /// The filtering logic runs on the original file, and matching images are removed from both files.
    /// </summary>
    /// <param name="originalCbzPath">Path to the original CBZ file</param>
    /// <param name="upscaledCbzPath">Path to the upscaled CBZ file (optional)</param>
    /// <param name="filters">List of filtered images to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing information about filtered images</returns>
    Task<ImageFilterResult> ApplyFiltersToChapterAsync(string originalCbzPath, string? upscaledCbzPath, IEnumerable<FilteredImage> filters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a filtered image entry from an image file, including thumbnail generation
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="library">Library to associate the filter with</param>
    /// <param name="description">Optional description</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created FilteredImage entity</returns>
    Task<FilteredImage> CreateFilteredImageFromFileAsync(string imagePath, Library library, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a filtered image entry from an image within a CBZ file
    /// </summary>
    /// <param name="cbzPath">Path to the CBZ file</param>
    /// <param name="imageEntryName">Name of the image entry within the CBZ</param>
    /// <param name="library">Library to associate the filter with</param>
    /// <param name="description">Optional description</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created FilteredImage entity</returns>
    Task<FilteredImage> CreateFilteredImageFromCbzAsync(string cbzPath, string imageEntryName, Library library, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a filtered image entry from raw image bytes
    /// </summary>
    /// <param name="imageBytes">Raw image bytes</param>
    /// <param name="fileName">Original filename</param>
    /// <param name="library">Library to associate the filter with</param>
    /// <param name="mimeType">MIME type of the image</param>
    /// <param name="description">Optional description</param>
    /// <returns>The created FilteredImage entity</returns>
    Task<FilteredImage> CreateFilteredImageFromBytesAsync(byte[] imageBytes, string fileName, Library library, string? mimeType = null, string? description = null);

    /// <summary>
    /// Generates a thumbnail for display purposes
    /// </summary>
    /// <param name="imageBytes">Original image bytes</param>
    /// <param name="maxSize">Maximum thumbnail dimension</param>
    /// <returns>Base64 encoded thumbnail</returns>
    Task<string> GenerateThumbnailBase64Async(byte[] imageBytes, int maxSize = 150);

    /// <summary>
    /// Calculates MD5 hash of image content for duplicate detection
    /// </summary>
    /// <param name="imageBytes">Image bytes</param>
    /// <returns>MD5 hash string</returns>
    string CalculateContentHash(byte[] imageBytes);

    /// <summary>
    /// Calculates perceptual hash for finding visually similar images
    /// </summary>
    /// <param name="imageBytes">Image bytes</param>
    /// <returns>Perceptual hash as ulong value</returns>
    ulong CalculatePerceptualHash(byte[] imageBytes);

    /// <summary>
    /// Calculates similarity percentage between two perceptual hashes
    /// </summary>
    /// <param name="hash1">First hash</param>
    /// <param name="hash2">Second hash</param>
    /// <returns>Similarity percentage (0-100)</returns>
    double CalculateImageSimilarity(ulong hash1, ulong hash2);

    /// <summary>
    /// Calculates Hamming distance between two perceptual hashes (legacy method)
    /// </summary>
    /// <param name="hash1">First hash</param>
    /// <param name="hash2">Second hash</param>
    /// <returns>Approximate Hamming distance</returns>
    int CalculateHammingDistance(ulong hash1, ulong hash2);
}
