namespace MangaIngestWithUpscaling.Data.LibraryManagement
{
    /// <summary>
    /// Represents a pattern-based filter rule for a library,
    /// such as a regex or glob, specifying which field to check.
    /// </summary>
    public class LibraryFilterRule
    {
        public int Id { get; set; }
        public int LibraryId { get; set; }
        public Library Library { get; set; }

        public string Pattern { get; set; }
        public LibraryFilterPatternType PatternType { get; set; }
        public LibraryFilterTargetField TargetField { get; set; }
    }

    /// <summary>
    /// Defines valid pattern types for library filter rules.
    /// </summary>
    public enum LibraryFilterPatternType
    {
        Regex,
        Glob
    }

    /// <summary>
    /// Defines possible fields that a filter rule might target.
    /// </summary>
    public enum LibraryFilterTargetField
    {
        FilePath,
        MangaTitle,
        MangaAuthor
    }

}