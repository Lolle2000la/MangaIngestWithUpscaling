using System.ComponentModel.DataAnnotations;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a blocked/filtered image that should be removed from chapters during ingest and retroactively.
/// </summary>
public class FilteredImage
{
    private int _occurrenceCount;
    public int Id { get; set; }

    /// <summary>
    /// The library this filtered image belongs to
    /// </summary>
    public int LibraryId { get; set; }

    public required Library Library { get; set; }

    /// <summary>
    /// Original filename (with extension) of the filtered image
    /// </summary>
    [Required]
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// Base64 encoded thumbnail preview of the image (for display in GUI)
    /// </summary>
    public string? ThumbnailBase64 { get; set; }

    /// <summary>
    /// MIME type of the original image
    /// </summary>
    public string? MimeType { get; set; }

    /// <summary>
    /// Original file size in bytes
    /// </summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// When this filter was added
    /// </summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional description/notes about why this image was filtered
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// MD5 hash of the original image content for duplicate detection
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Perceptual hash for finding visually similar images (64-bit hash)
    /// </summary>
    public ulong? PerceptualHash { get; set; }

    /// <summary>
    /// Number of times this filtered image has been found and removed
    /// </summary>
    public int OccurrenceCount { get => _occurrenceCount; set => _occurrenceCount = value; }

    /// <summary>
    /// Last time this filter was applied/matched
    /// </summary>
    public DateTime? LastMatchedAt { get; set; }

    public int IncrementOccurrenceCountThreadSafe()
    {
        return Interlocked.Increment(ref _occurrenceCount);
    }
}