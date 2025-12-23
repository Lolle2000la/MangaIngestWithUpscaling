using System.ComponentModel.DataAnnotations;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a pattern-based renaming rule for a library,
/// specifying a target field, a pattern to match, and a replacement.
/// </summary>
public class LibraryRenameRule
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public Library Library { get; set; } = default!;

    public string Pattern { get; set; } = string.Empty;
    public LibraryRenamePatternType PatternType { get; set; }
    public LibraryRenameTargetField TargetField { get; set; }
    public string Replacement { get; set; } = string.Empty;
}

/// <summary>
/// Defines valid pattern types for library rename rules.
/// </summary>
public enum LibraryRenamePatternType
{
    Regex,
    Contains,
}

/// <summary>
/// Defines possible fields that a rename rule might target.
/// </summary>
public enum LibraryRenameTargetField
{
    [Display(Name = "Series Title")]
    SeriesTitle,

    [Display(Name = "File Name")]
    FileName,

    [Display(Name = "Chapter Title")]
    ChapterTitle,
}
