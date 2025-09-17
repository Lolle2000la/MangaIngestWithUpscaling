namespace MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

/// <summary>
/// Result of analyzing differences between two CBZ files
/// </summary>
public class PageDifferenceResult
{
    /// <summary>
    /// Page names that exist in the original but are missing from the upscaled version
    /// </summary>
    public IReadOnlyList<string> MissingPages { get; }

    /// <summary>
    /// Page names that exist in the upscaled version but not in the original
    /// </summary>
    public IReadOnlyList<string> ExtraPages { get; }

    /// <summary>
    /// Whether the files have identical page sets
    /// </summary>
    public bool AreEqual => MissingPages.Count == 0 && ExtraPages.Count == 0;

    /// <summary>
    /// Whether repair is possible (has missing pages but no extra pages, or only has extra pages)
    /// </summary>
    public bool CanRepair => MissingPages.Count > 0 || ExtraPages.Count > 0;

    public PageDifferenceResult(IEnumerable<string> missingPages, IEnumerable<string> extraPages)
    {
        MissingPages = missingPages.ToList().AsReadOnly();
        ExtraPages = extraPages.ToList().AsReadOnly();
    }
}