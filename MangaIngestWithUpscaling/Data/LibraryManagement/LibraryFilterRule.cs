using System.ComponentModel.DataAnnotations;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a pattern-based filter rule for a library,
/// such as a regex or glob, specifying which field to check.
/// </summary>
public class LibraryFilterRule
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public required Library Library { get; set; }

    public string Pattern { get; set; } = string.Empty;
    public LibraryFilterPatternType PatternType { get; set; }
    public LibraryFilterTargetField TargetField { get; set; }
    public FilterAction Action { get; set; }
}

/// <summary>
/// Defines valid pattern types for library filter rules.
/// </summary>
public enum LibraryFilterPatternType
{
    Regex,
    Contains,
}

/// <summary>
/// Whether the rule includes or excludes certain chapters.
/// </summary>
public enum FilterAction
{
    Include,
    Exclude,
}

/// <summary>
/// Defines possible fields that a filter rule might target.
/// </summary>
public enum LibraryFilterTargetField
{
    [Display(Name = "File Path")]
    FilePath,

    [Display(Name = "Manga Title")]
    MangaTitle,

    [Display(Name = "Chapter Title")]
    ChapterTitle,
}
