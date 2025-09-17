using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Tests.Services.LibraryFiltering;

public class LibraryFilteringServiceTests
{
    private readonly LibraryFilteringService _service;

    public LibraryFilteringServiceTests()
    {
        _service = new LibraryFilteringService();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithNoRules_ShouldReturnFalse()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("Test Series", "Chapter 1", "/path/to/chapter.cbz");
        var rules = new List<LibraryFilterRule>();

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithExcludeRuleMatch_ShouldReturnTrue()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("Naruto", "Chapter 1", "/manga/naruto/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "Naruto",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithExcludeRuleNoMatch_ShouldReturnFalse()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "Naruto",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithIncludeRuleMatch_ShouldReturnFalse()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "One Piece",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Include
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithIncludeRuleNoMatch_ShouldReturnTrue()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("Naruto", "Chapter 1", "/manga/naruto/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "One Piece",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Include
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithRegexPattern_ShouldMatchCorrectly()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 001", "/manga/onepiece/chapter001.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                @"Chapter \d{3}",
                LibraryFilterPatternType.Regex,
                LibraryFilterTargetField.ChapterTitle,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithInvalidRegexPattern_ShouldThrowException()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "[invalid regex",
                LibraryFilterPatternType.Regex,
                LibraryFilterTargetField.ChapterTitle,
                FilterAction.Exclude
            )
        };

        // Act & Assert
        Assert.Throws<RegexParseException>(() => _service.FilterChapter(chapter, rules));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithFilePathTarget_ShouldMatchPath()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/special/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "special",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.FilePath,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithNullSeriesTitle_ShouldNotMatch()
    {
        // Arrange
        var chapter = CreateTestFoundChapter(null, "Chapter 1", "/manga/unknown/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "Unknown",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithNullChapterTitle_ShouldNotMatch()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", null, "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "Chapter",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.ChapterTitle,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithMixedRules_ExcludeTakesPrecedence()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "One Piece",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Include
            ),
            CreateTestLibraryFilterRule(
                "Chapter 1",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.ChapterTitle,
                FilterAction.Exclude
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.True(result); // Exclude rule should take precedence
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithMultipleIncludeRules_OneMatchShouldPass()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                "Naruto",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Include
            ),
            CreateTestLibraryFilterRule(
                "One Piece",
                LibraryFilterPatternType.Contains,
                LibraryFilterTargetField.MangaTitle,
                FilterAction.Include
            )
        };

        // Act
        var result = _service.FilterChapter(chapter, rules);

        // Assert
        Assert.False(result); // Should pass because one include rule matches
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithInvalidTargetField_ShouldThrowNotImplementedException()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                pattern: "test",
                patternType: LibraryFilterPatternType.Contains,
                targetField: (LibraryFilterTargetField)999, // Invalid enum value
                action: FilterAction.Exclude
            )
        };

        // Act & Assert
        Assert.Throws<NotImplementedException>(() => _service.FilterChapter(chapter, rules));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterChapter_WithInvalidPatternType_ShouldThrowNotImplementedException()
    {
        // Arrange
        var chapter = CreateTestFoundChapter("One Piece", "Chapter 1", "/manga/onepiece/chapter1.cbz");
        var rules = new List<LibraryFilterRule>
        {
            CreateTestLibraryFilterRule(
                pattern: "test",
                patternType: (LibraryFilterPatternType)999, // Invalid enum value
                targetField: LibraryFilterTargetField.MangaTitle,
                action: FilterAction.Exclude
            )
        };

        // Act & Assert
        Assert.Throws<NotImplementedException>(() => _service.FilterChapter(chapter, rules));
    }

    private static LibraryFilterRule CreateTestLibraryFilterRule(
        string pattern, 
        LibraryFilterPatternType patternType, 
        LibraryFilterTargetField targetField, 
        FilterAction action)
    {
        return new LibraryFilterRule
        {
            Pattern = pattern,
            PatternType = patternType,
            TargetField = targetField,
            Action = action,
            Library = new Library { Id = 1, Name = "Test Library", IngestPath = "/test" }
        };
    }

    private static FoundChapter CreateTestFoundChapter(string? seriesTitle, string? chapterTitle, string relativePath)
    {
        var metadata = new ExtractedMetadata(
            seriesTitle ?? "Default Series",
            chapterTitle,
            null
        );

        return new FoundChapter(
            Path.GetFileName(relativePath),
            relativePath,
            ChapterStorageType.Cbz,
            metadata
        );
    }
}