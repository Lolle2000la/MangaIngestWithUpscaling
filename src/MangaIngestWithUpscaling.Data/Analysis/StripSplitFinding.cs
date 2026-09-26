using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Data.Analysis;

public class StripSplitFinding
{
    public int Id { get; set; }

    public int ChapterId { get; set; }

    [ForeignKey(nameof(ChapterId))]
    public Chapter Chapter { get; set; } = null!;

    /// <summary>
    /// The base filename of the page (without extension) to identify the page
    /// regardless of format changes (e.g. jpg -> avif).
    /// </summary>
    [Required]
    public string PageFileName { get; set; } = string.Empty;

    /// <summary>
    /// The raw JSON output from the split detector.
    /// </summary>
    [Required]
    public string SplitJson { get; set; } = string.Empty;

    /// <summary>
    /// The version of the detector logic used to find these splits.
    /// </summary>
    public int DetectorVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
