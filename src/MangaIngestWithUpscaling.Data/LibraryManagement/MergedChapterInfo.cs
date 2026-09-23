using System.ComponentModel.DataAnnotations;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
///     Stores information about merged chapter parts to enable reversion
/// </summary>
public class MergedChapterInfo
{
    public int Id { get; set; }

    [Required]
    public int ChapterId { get; set; }

    public Chapter Chapter { get; set; } = default!;

    /// <summary>
    ///     List of original chapter part information
    /// </summary>
    [Required]
    public List<OriginalChapterPart> OriginalParts { get; set; } = new();

    /// <summary>
    ///     The merged chapter number (e.g., "22" when parts were "22.1", "22.2", etc.)
    /// </summary>
    [Required]
    public string MergedChapterNumber { get; set; } = string.Empty;

    /// <summary>
    ///     When this merge was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Information about an original chapter part for restoration
/// </summary>
public class OriginalChapterPart
{
    /// <summary>
    ///     Original filename
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    ///     Original chapter number (e.g., "22.1")
    /// </summary>
    public string ChapterNumber { get; set; } = string.Empty;

    /// <summary>
    ///     Original metadata from ComicInfo.xml
    /// </summary>
    public ExtractedMetadata Metadata { get; set; } = new("", null, null);

    /// <summary>
    ///     Original raw ComicInfo.xml content (for complete metadata preservation during restoration)
    /// </summary>
    public string? OriginalComicInfoXml { get; set; }

    /// <summary>
    ///     Names of pages in the original order (for proper page ordering when reverting)
    /// </summary>
    public List<string> PageNames { get; set; } = new();

    /// <summary>
    ///     Page range in the merged CBZ (start and end index)
    /// </summary>
    public int StartPageIndex { get; set; }

    public int EndPageIndex { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not OriginalChapterPart other)
        {
            return false;
        }

        return FileName == other.FileName
            && ChapterNumber == other.ChapterNumber
            && StartPageIndex == other.StartPageIndex
            && EndPageIndex == other.EndPageIndex
            && PageNames.SequenceEqual(other.PageNames)
            && OriginalComicInfoXml == other.OriginalComicInfoXml;
    }

    public override int GetHashCode()
    {
        int hash = HashCode.Combine(
            FileName,
            ChapterNumber,
            StartPageIndex,
            EndPageIndex,
            OriginalComicInfoXml
        );
        foreach (string pageName in PageNames)
        {
            hash = HashCode.Combine(hash, pageName);
        }

        return hash;
    }
}
