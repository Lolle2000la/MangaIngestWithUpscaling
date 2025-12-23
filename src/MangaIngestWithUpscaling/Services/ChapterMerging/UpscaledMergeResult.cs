namespace MangaIngestWithUpscaling.Services.ChapterMerging;

/// <summary>
/// Result of upscaled chapter merging operations
/// </summary>
public record UpscaledMergeResult
{
    /// <summary>
    /// Whether any upscaled content was merged
    /// </summary>
    public bool HasUpscaledContent { get; init; }

    /// <summary>
    /// Whether the merge was partial (some parts were missing upscaled versions)
    /// </summary>
    public bool IsPartialMerge { get; init; }

    /// <summary>
    /// Number of parts that had upscaled versions
    /// </summary>
    public int UpscaledPartsCount { get; init; }

    /// <summary>
    /// Number of parts that were missing upscaled versions
    /// </summary>
    public int MissingPartsCount { get; init; }

    /// <summary>
    /// Total number of parts in the merge
    /// </summary>
    public int TotalPartsCount { get; init; }

    /// <summary>
    /// Creates a result for no upscaled content
    /// </summary>
    public static UpscaledMergeResult NoUpscaledContent() =>
        new()
        {
            HasUpscaledContent = false,
            IsPartialMerge = false,
            UpscaledPartsCount = 0,
            MissingPartsCount = 0,
            TotalPartsCount = 0,
        };

    /// <summary>
    /// Creates a result for complete upscaled merge (all parts had upscaled versions)
    /// </summary>
    public static UpscaledMergeResult CompleteMerge(int totalParts) =>
        new()
        {
            HasUpscaledContent = true,
            IsPartialMerge = false,
            UpscaledPartsCount = totalParts,
            MissingPartsCount = 0,
            TotalPartsCount = totalParts,
        };

    /// <summary>
    /// Creates a result for partial upscaled merge (some parts had upscaled versions, some didn't)
    /// </summary>
    public static UpscaledMergeResult PartialMerge(int upscaledParts, int missingParts) =>
        new()
        {
            HasUpscaledContent = true,
            IsPartialMerge = true,
            UpscaledPartsCount = upscaledParts,
            MissingPartsCount = missingParts,
            TotalPartsCount = upscaledParts + missingParts,
        };
}
