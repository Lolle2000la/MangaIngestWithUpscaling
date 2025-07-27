namespace MangaIngestWithUpscaling.Services.ChapterMerging;

/// <summary>
///     Result of checking if chapters can be merged considering their upscale status
/// </summary>
public record UpscaleCompatibilityResult(
    bool CanMerge,
    string? Reason = null);