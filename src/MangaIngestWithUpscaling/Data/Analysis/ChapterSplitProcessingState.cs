using System.ComponentModel.DataAnnotations.Schema;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Data.Analysis;

public enum SplitProcessingStatus
{
    Pending = 0,
    Detected = 1,
    Applied = 2,
    Failed = 3,
}

public class ChapterSplitProcessingState
{
    public int Id { get; set; }

    public int ChapterId { get; set; }

    [ForeignKey(nameof(ChapterId))]
    public Chapter Chapter { get; set; } = null!;

    /// <summary>
    /// The detector version that was last successfully run on this chapter.
    /// </summary>
    public int LastProcessedDetectorVersion { get; set; }

    /// <summary>
    /// The detector version whose splits were last applied to this chapter.
    /// </summary>
    public int LastAppliedDetectorVersion { get; set; }

    public SplitProcessingStatus Status { get; set; }

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
