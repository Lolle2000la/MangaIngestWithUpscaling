namespace MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

/// <summary>
/// Represents the result of comparing pages between two CBZ archives.
/// </summary>
public class PageComparisonResult
{
    /// <summary>
    /// Whether the pages are equal (same count and same page names).
    /// </summary>
    public bool PagesEqual { get; set; }
    
    /// <summary>
    /// Page names that exist in the first file but not in the second.
    /// </summary>
    public List<string> MissingFromSecond { get; set; } = new();
    
    /// <summary>
    /// Page names that exist in the second file but not in the first.
    /// </summary>
    public List<string> ExtraInSecond { get; set; } = new();
    
    /// <summary>
    /// Page names that exist in both files.
    /// </summary>
    public List<string> CommonPages { get; set; } = new();
}