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
    /// Whether the files have identical page sets. Also false when either archive could not be read
    /// (<see cref="Corrupt"/> / <see cref="ReadFailed"/>): an unreadable archive is not "no
    /// differences", and a caller that checks only this must not silently complete the work.
    /// </summary>
    public bool AreEqual =>
        !Corrupt && !ReadFailed && MissingPages.Count == 0 && ExtraPages.Count == 0;

    /// <summary>
    /// Whether repair is possible (has missing pages but no extra pages, or only has extra pages).
    /// False when an archive could not be read, since a repair decision cannot be made from an
    /// unreadable archive.
    /// </summary>
    public bool CanRepair =>
        !Corrupt && !ReadFailed && (MissingPages.Count > 0 || ExtraPages.Count > 0);

    /// <summary>
    /// True when the analysis could not read one of the archives because it is malformed, rather than
    /// because the page sets match. Callers that must tell "no differences" from "corrupt" (the repair
    /// path) check this.
    /// </summary>
    public bool Corrupt { get; init; }

    /// <summary>
    /// True when the analysis could not read one of the archives for a reason that may be transient
    /// (an I/O failure, a lock, a truncated stream) rather than because the page sets match. Callers
    /// must not treat this as "no differences" and silently complete the work.
    /// </summary>
    public bool ReadFailed { get; init; }

    public PageDifferenceResult(IEnumerable<string> missingPages, IEnumerable<string> extraPages)
    {
        MissingPages = missingPages.ToList().AsReadOnly();
        ExtraPages = extraPages.ToList().AsReadOnly();
    }
}
