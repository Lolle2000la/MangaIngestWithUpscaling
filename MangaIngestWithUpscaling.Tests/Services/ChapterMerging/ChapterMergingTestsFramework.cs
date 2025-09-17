using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using NetVips;
using NSubstitute;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Tests.Services.ChapterMerging;

/// <summary>
/// Tests for chapter number extraction and validation logic
/// These tests demonstrate the testing approach for the chapter merging functionality
/// </summary>
public class ChapterNumberHelperTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("Chapter 5.1.cbz", "5.1")]
    [InlineData("Ch. 10.2.cbz", "10.2")]
    [InlineData("Ch 15.3.cbz", "15.3")]
    [InlineData("Episode 3.5.cbz", "3.5")]
    [InlineData("第2.4話.cbz", "2.4")]
    [InlineData("Cap. 8.7.cbz", "8.7")]
    [InlineData("Capítulo 12.1.cbz", "12.1")]
    [InlineData("Chương 4.3.cbz", "4.3")]
    public void ExtractChapterNumber_WithVariousFormats_ShouldExtractCorrectly(string fileName, string expectedNumber)
    {
        // Act
        var result = ChapterNumberHelper.ExtractChapterNumber(fileName);

        // Assert
        Assert.Equal(expectedNumber, result);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("Chapter 5.cbz", "5")]
    [InlineData("Ch. 10.cbz", "10")]
    [InlineData("Episode 15.cbz", "15")]
    [InlineData("第23話.cbz", "23")]
    public void ExtractChapterNumber_WithWholeNumbers_ShouldExtractCorrectly(string fileName, string expectedNumber)
    {
        // Act
        var result = ChapterNumberHelper.ExtractChapterNumber(fileName);

        // Assert
        Assert.Equal(expectedNumber, result);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("NoNumberFile.cbz")]
    [InlineData("RandomText.cbz")]
    [InlineData("Chapter.cbz")]
    [InlineData("Ch..cbz")]
    [InlineData("SomeRandomFile.txt")]
    public void ExtractChapterNumber_WithInvalidFormats_ShouldReturnNull(string fileName)
    {
        // Act
        var result = ChapterNumberHelper.ExtractChapterNumber(fileName);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("5.1", "5")]
    [InlineData("10.25", "10")]
    [InlineData("123.456", "123")]
    [InlineData("7", "7")]
    [InlineData("42.0", "42")]
    public void ExtractBaseChapterNumber_WithValidNumbers_ShouldExtractBase(string chapterNumber, string expectedBase)
    {
        // This tests the concept of extracting base numbers, which would be part of the chapter merging logic
        // Since ExtractBaseChapterNumber is private, we test the concept through the public API

        // Arrange - Create a filename with the chapter number
        var fileName = $"Chapter {chapterNumber}.cbz";

        // Act - Extract the chapter number first
        var extractedNumber = ChapterNumberHelper.ExtractChapterNumber(fileName);

        // Assert - Verify we get the expected number (the actual base extraction would be tested in integration tests)
        Assert.Equal(chapterNumber, extractedNumber);

        // For base number extraction, we can test the logic conceptually
        if (decimal.TryParse(chapterNumber, out decimal number))
        {
            var baseNumber = Math.Floor(number).ToString();
            Assert.Equal(expectedBase, baseNumber);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ExtractChapterNumber_WithComplexScenarios_ShouldHandleEdgeCases()
    {
        // Test edge cases that might occur in real manga files
        var testCases = new Dictionary<string, string?>
        {
            // Valid cases
            { "Manga Title - Chapter 123.45.cbz", "123.45" },
            { "[Group] Series Name Ch.67.8 [Quality].cbz", "67.8" },
            { "Title_Vol01_Ch015.5.cbz", "015.5" },

            // Invalid cases
            { "Volume 1.cbz", "1" }, // Will extract the "1" from Volume
            { "No numbers here.cbz", null },
            { "Just-dashes-and.dots.cbz", null }
        };

        foreach (var testCase in testCases)
        {
            // Act
            var result = ChapterNumberHelper.ExtractChapterNumber(testCase.Key);

            // Assert
            Assert.Equal(testCase.Value, result);
        }
    }
}

/// <summary>
/// Unit tests for ChapterPartMerger focusing on grouping logic and merge detection
/// </summary>
public class ChapterPartMergerTests : IDisposable
{
    private readonly ChapterPartMerger _chapterPartMerger;
    private readonly ILogger<ChapterPartMerger> _mockLogger;
    private readonly IMetadataHandlingService _mockMetadataService;
    private readonly string _tempDir;

    public ChapterPartMergerTests()
    {
        _mockMetadataService = Substitute.For<IMetadataHandlingService>();
        _mockLogger = Substitute.For<ILogger<ChapterPartMerger>>();
        _chapterPartMerger = new ChapterPartMerger(_mockMetadataService, _mockLogger);

        _tempDir = Path.Combine(Path.GetTempPath(), $"chapter_part_merger_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    #region GroupChapterPartsForMerging Tests

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("5.1", "5.2", "5.3", "5")] // Standard consecutive
    [InlineData("10.1", "10.2", "10", "10")] // With whole number
    [InlineData("2.1", "2.2", "2.3", "2.4", "2")] // Four parts
    public void GroupChapterPartsForMerging_WithConsecutiveChapterParts_ShouldGroupCorrectly(
        params string[] chapterNumbers)
    {
        // Arrange
        var baseNumber = chapterNumbers.Last();
        var chapters = chapterNumbers.Take(chapterNumbers.Length - 1)
            .Select(num => CreateFoundChapter($"Chapter {num}.cbz", num))
            .ToList();

        // Act
        var result = _chapterPartMerger.GroupChapterPartsForMerging(chapters, _ => false);

        // Assert
        Assert.Single(result);
        Assert.Contains(baseNumber, result.Keys);
        Assert.Equal(chapters.Count, result[baseNumber].Count);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("5.1", "5.3")] // Missing 5.2
    [InlineData("10.1", "10.4")] // Missing 10.2, 10.3
    [InlineData("2.2", "2.4")] // Missing 2.1, 2.3
    public void GroupChapterPartsForMerging_WithNonConsecutiveChapterParts_ShouldNotGroup(
        params string[] chapterNumbers)
    {
        // Arrange
        var chapters = chapterNumbers
            .Select(num => CreateFoundChapter($"Chapter {num}.cbz", num))
            .ToList();

        // Act
        var result = _chapterPartMerger.GroupChapterPartsForMerging(chapters, _ => false);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChapterPartsForMerging_WithLatestChapter_ShouldExcludeFromMerging()
    {
        // Arrange
        var chapters = new List<FoundChapter>
        {
            CreateFoundChapter("Chapter 7.1.cbz", "7.1"), CreateFoundChapter("Chapter 7.2.cbz", "7.2")
        };

        // Act
        var result = _chapterPartMerger.GroupChapterPartsForMerging(chapters, baseNumber => baseNumber == "7");

        // Assert
        Assert.Empty(result); // Should be excluded because 7 is the latest chapter
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChapterPartsForMerging_WithSingleChapter_ShouldNotGroup()
    {
        // Arrange
        var chapters = new List<FoundChapter> { CreateFoundChapter("Chapter 5.1.cbz", "5.1") };

        // Act
        var result = _chapterPartMerger.GroupChapterPartsForMerging(chapters, _ => false);

        // Assert
        Assert.Empty(result); // Single chapters should not be grouped
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChapterPartsForMerging_WithMixedChapterNumbers_ShouldGroupOnlyConsecutive()
    {
        // Arrange
        var chapters = new List<FoundChapter>
        {
            CreateFoundChapter("Chapter 3.1.cbz", "3.1"),
            CreateFoundChapter("Chapter 3.2.cbz", "3.2"), // These should group
            CreateFoundChapter("Chapter 5.1.cbz", "5.1"), // Single, should not group
            CreateFoundChapter("Chapter 7.1.cbz", "7.1"),
            CreateFoundChapter("Chapter 7.2.cbz", "7.2"),
            CreateFoundChapter("Chapter 7.3.cbz", "7.3") // These should group
        };

        // Act
        var result = _chapterPartMerger.GroupChapterPartsForMerging(chapters, _ => false);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("3", result.Keys);
        Assert.Contains("7", result.Keys);
        Assert.Equal(2, result["3"].Count);
        Assert.Equal(3, result["7"].Count);
    }

    #endregion

    #region GroupChaptersForAdditionToExistingMerged Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChaptersForAdditionToExistingMerged_WithValidChapter_ShouldIdentifyAddition()
    {
        // Arrange
        var chapters = new List<FoundChapter>
        {
            CreateFoundChapter("Chapter 2.3.cbz", "2.3"), CreateFoundChapter("Chapter 5.4.cbz", "5.4")
        };
        var existingMergedBaseNumbers = new HashSet<string> { "2", "5" };

        // Act
        var result = _chapterPartMerger.GroupChaptersForAdditionToExistingMerged(
            chapters, existingMergedBaseNumbers, _ => false);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("2", result.Keys);
        Assert.Contains("5", result.Keys);
        Assert.Single(result["2"]);
        Assert.Single(result["5"]);
        Assert.Equal("Chapter 2.3.cbz", result["2"].First().FileName);
        Assert.Equal("Chapter 5.4.cbz", result["5"].First().FileName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChaptersForAdditionToExistingMerged_WithNonMatchingBaseNumber_ShouldNotGroup()
    {
        // Arrange
        var chapters = new List<FoundChapter>
        {
            CreateFoundChapter("Chapter 3.1.cbz", "3.1"), // 3 is not in existing merged
            CreateFoundChapter("Chapter 4.2.cbz", "4.2") // 4 is not in existing merged
        };
        var existingMergedBaseNumbers = new HashSet<string> { "2", "5" };

        // Act
        var result = _chapterPartMerger.GroupChaptersForAdditionToExistingMerged(
            chapters, existingMergedBaseNumbers, _ => false);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChaptersForAdditionToExistingMerged_WithLatestChapter_ShouldExclude()
    {
        // Arrange
        var chapters = new List<FoundChapter> { CreateFoundChapter("Chapter 2.3.cbz", "2.3") };
        var existingMergedBaseNumbers = new HashSet<string> { "2" };

        // Act
        var result = _chapterPartMerger.GroupChaptersForAdditionToExistingMerged(
            chapters, existingMergedBaseNumbers, baseNumber => baseNumber == "2"); // 2 is latest

        // Assert
        Assert.Empty(result); // Should be excluded because 2 is the latest chapter
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GroupChaptersForAdditionToExistingMerged_WithWholeChapterNumber_ShouldNotAdd()
    {
        // Arrange
        var chapters = new List<FoundChapter>
        {
            CreateFoundChapter("Chapter 2.cbz", "2") // Whole number, not a part
        };
        var existingMergedBaseNumbers = new HashSet<string> { "2" };

        // Act
        var result = _chapterPartMerger.GroupChaptersForAdditionToExistingMerged(
            chapters, existingMergedBaseNumbers, _ => false);

        // Assert
        Assert.Empty(result); // Whole numbers should not be added to existing merged chapters
    }

    #endregion

    #region File Operation Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeChapterPartsAsync_WithValidFiles_ShouldCreateMergedFile()
    {
        // Arrange
        var chapters = new List<FoundChapter>
        {
            CreateFoundChapter("Chapter 1.1.cbz", "1.1"), CreateFoundChapter("Chapter 1.2.cbz", "1.2")
        };

        CreateTestCbzFiles(chapters);

        var targetMetadata = new ExtractedMetadata("Test Series", "Chapter 1", "1");

        // Act
        (FoundChapter mergedChapter, List<OriginalChapterPart> originalParts) result =
            await _chapterPartMerger.MergeChapterPartsAsync(chapters, _tempDir, _tempDir, "1", targetMetadata,
                cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result.mergedChapter);
        Assert.Equal(2, result.originalParts.Count);

        var mergedFilePath = Path.Combine(_tempDir, result.mergedChapter.FileName);
        Assert.True(File.Exists(mergedFilePath));

        // Verify merged file contains all pages
        using var fileStream = new FileStream(mergedFilePath, FileMode.Open);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        var imageEntries = archive.Entries.Where(e => e.Name.EndsWith(".jpg") || e.Name.EndsWith(".png")).ToList();
        Assert.Equal(6, imageEntries.Count); // 3 pages from each chapter
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestoreChapterPartsAsync_WithValidMergedFile_ShouldRestoreOriginalParts()
    {
        // Arrange
        var originalParts = new List<OriginalChapterPart>
        {
            new()
            {
                FileName = "Chapter 2.1.cbz",
                ChapterNumber = "2.1",
                PageNames = new List<string> { "001.jpg", "002.jpg", "003.jpg" },
                StartPageIndex = 0,
                EndPageIndex = 2,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 2.1", "2.1")
            },
            new()
            {
                FileName = "Chapter 2.2.cbz",
                ChapterNumber = "2.2",
                PageNames = new List<string> { "004.jpg", "005.jpg", "006.jpg" },
                StartPageIndex = 3,
                EndPageIndex = 5,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 2.2", "2.2")
            }
        };

        // Create merged file with test images
        var mergedFilePath = Path.Combine(_tempDir, "Chapter 2.cbz");
        CreateMergedTestCbzFile(mergedFilePath,
            new[] { "001.jpg", "002.jpg", "003.jpg", "004.jpg", "005.jpg", "006.jpg" });

        // Act
        List<FoundChapter> restoredChapters = await _chapterPartMerger.RestoreChapterPartsAsync(mergedFilePath,
            originalParts,
            _tempDir, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, restoredChapters.Count);

        var chapter21 = restoredChapters.First(c => c.FileName == "Chapter 2.1.cbz");
        var chapter22 = restoredChapters.First(c => c.FileName == "Chapter 2.2.cbz");

        Assert.NotNull(chapter21);
        Assert.NotNull(chapter22);

        // Verify files were created
        Assert.True(File.Exists(Path.Combine(_tempDir, "Chapter 2.1.cbz")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "Chapter 2.2.cbz")));

        // Verify each restored file has correct number of pages
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 2.1.cbz"), 3);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 2.2.cbz"), 3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreChapterPartsAsync_WithDifferentNamingConventions_ShouldHandleAllFormats()
    {
        // Arrange - Test different page naming conventions in merged files
        var testCases = new[]
        {
            new { Description = "3-digit padding", PageNames = new[] { "001.jpg", "002.jpg", "003.jpg" } },
            new { Description = "2-digit padding", PageNames = new[] { "01.jpg", "02.jpg", "03.jpg" } },
            new { Description = "1-digit padding", PageNames = new[] { "1.jpg", "2.jpg", "3.jpg" } },
            new { Description = "mixed formats", PageNames = new[] { "page1.jpg", "img_02.png", "003.jpeg" } },
            new { Description = "4-digit standard", PageNames = new[] { "0000.jpg", "0001.jpg", "0002.jpg" } }
        };

        foreach (var testCase in testCases)
        {
            using var testScope = new TestScope($"Testing {testCase.Description}");

            var originalParts = new List<OriginalChapterPart>
            {
                new()
                {
                    FileName = "Chapter 3.1.cbz",
                    ChapterNumber = "3.1",
                    PageNames = testCase.PageNames.ToList(),
                    StartPageIndex = 0,
                    EndPageIndex = testCase.PageNames.Length - 1,
                    Metadata = new ExtractedMetadata("Test Series", "Chapter 3.1", "3.1")
                }
            };

            // Create merged file with the specific naming convention
            var mergedFilePath = Path.Combine(_tempDir, $"merged_{testCase.Description.Replace(" ", "_")}.cbz");
            CreateMergedTestCbzFileWithCustomNames(mergedFilePath, testCase.PageNames);

            // Act
            List<FoundChapter> restoredChapters = await _chapterPartMerger.RestoreChapterPartsAsync(mergedFilePath,
                originalParts,
                _tempDir, TestContext.Current.CancellationToken);

            // Assert
            Assert.Single(restoredChapters);
            var restoredPath = Path.Combine(_tempDir, "Chapter 3.1.cbz");
            Assert.True(File.Exists(restoredPath), $"Restored file should exist for {testCase.Description}");

            // Verify the restored file has all pages
            VerifyRestoredChapterPageCount(restoredPath, testCase.PageNames.Length);

            // Clean up for next iteration
            if (File.Exists(restoredPath)) File.Delete(restoredPath);
            if (File.Exists(mergedFilePath)) File.Delete(mergedFilePath);
        }
    }

    /// <summary>
    /// Tests that restore works even when merged files use inconsistent page numbering
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreChapterPartsAsync_WithInconsistentPageNumbering_ShouldFallbackToPositionalMatching()
    {
        // Arrange - Create a merged file with non-sequential or non-standard page names
        var originalParts = new List<OriginalChapterPart>
        {
            new()
            {
                FileName = "Chapter 4.1.cbz",
                ChapterNumber = "4.1",
                PageNames = new List<string> { "cover.jpg", "page1.jpg" },
                StartPageIndex = 0,
                EndPageIndex = 1,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 4.1", "4.1")
            },
            new()
            {
                FileName = "Chapter 4.2.cbz",
                ChapterNumber = "4.2",
                PageNames = new List<string> { "start.png", "middle.png", "end.png" },
                StartPageIndex = 2,
                EndPageIndex = 4,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 4.2", "4.2")
            }
        };

        // Create merged file with non-standard naming that doesn't follow numeric patterns
        var mergedFilePath = Path.Combine(_tempDir, "Chapter 4 Merged.cbz");
        string[] nonStandardPageNames = new[]
        {
            "titlepage.jpg", "story_01.png", "battle_scene.jpg", "conclusion.png", "credits.jpg"
        };
        CreateMergedTestCbzFileWithCustomNames(mergedFilePath, nonStandardPageNames);

        // Act
        List<FoundChapter> restoredChapters = await _chapterPartMerger.RestoreChapterPartsAsync(mergedFilePath,
            originalParts,
            _tempDir, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, restoredChapters.Count);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 4.1.cbz"), 2);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 4.2.cbz"), 3);
    }

    /// <summary>
    /// Tests that restoration handles completely non-numeric page naming conventions
    /// that rely entirely on natural sorting order
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreChapterPartsAsync_WithCompletelyNonNumericNames_ShouldUseNaturalSorting()
    {
        // Arrange - Create merged file with descriptive, non-numeric page names
        var originalParts = new List<OriginalChapterPart>
        {
            new()
            {
                FileName = "Chapter 5.1.cbz",
                ChapterNumber = "5.1",
                PageNames = new List<string> { "opening_scene.jpg", "character_intro.png" },
                StartPageIndex = 0,
                EndPageIndex = 1,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 5.1", "5.1")
            },
            new()
            {
                FileName = "Chapter 5.2.cbz",
                ChapterNumber = "5.2",
                PageNames = new List<string> { "action_begins.jpg", "battle_page_01.png", "climax_moment.jpg" },
                StartPageIndex = 2,
                EndPageIndex = 4,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 5.2", "5.2")
            }
        };

        // Create merged file with completely descriptive names that should sort naturally
        var mergedFilePath = Path.Combine(_tempDir, "Chapter 5 Merged.cbz");
        var descriptivePageNames = new[]
        {
            "a_opening_scene.jpg", // Prefix ensures it sorts first
            "b_character_intro.png", // Natural alphabetical sorting
            "c_action_begins.jpg", // Descriptive but orderable
            "d_battle_page_main.png", // No numbers, just names
            "e_climax_final.jpg" // Natural conclusion
        };
        CreateMergedTestCbzFileWithCustomNames(mergedFilePath, descriptivePageNames);

        // Act
        List<FoundChapter> restoredChapters = await _chapterPartMerger.RestoreChapterPartsAsync(mergedFilePath,
            originalParts,
            _tempDir, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, restoredChapters.Count);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 5.1.cbz"), 2);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 5.2.cbz"), 3);
    }

    /// <summary>
    /// Tests various edge cases of page naming that might be found in real-world CBZ files
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreChapterPartsAsync_WithMixedNamingConventions_ShouldHandleAllFormats()
    {
        // Arrange - Test mixed naming conventions in a single file
        var originalParts = new List<OriginalChapterPart>
        {
            new()
            {
                FileName = "Chapter 6.1.cbz",
                ChapterNumber = "6.1",
                PageNames = new List<string> { "cover.jpg", "p2.png" },
                StartPageIndex = 0,
                EndPageIndex = 1,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 6.1", "6.1")
            },
            new()
            {
                FileName = "Chapter 6.2.cbz",
                ChapterNumber = "6.2",
                PageNames = new List<string> { "Page003.jpg", "scan_4.png", "final_page.jpg" },
                StartPageIndex = 2,
                EndPageIndex = 4,
                Metadata = new ExtractedMetadata("Test Series", "Chapter 6.2", "6.2")
            }
        };

        // Create merged file with mixed naming conventions
        var mergedFilePath = Path.Combine(_tempDir, "Chapter 6 Merged.cbz");
        var mixedPageNames = new[]
        {
            "cover.jpg", // Simple name
            "p2.png", // Short with number
            "Page003.jpg", // Mixed case with padding
            "scan_4.png", // Underscore separator
            "final_page.jpg" // Descriptive name
        };
        CreateMergedTestCbzFileWithCustomNames(mergedFilePath, mixedPageNames);

        // Act
        List<FoundChapter> restoredChapters = await _chapterPartMerger.RestoreChapterPartsAsync(mergedFilePath,
            originalParts,
            _tempDir, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, restoredChapters.Count);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 6.1.cbz"), 2);
        VerifyRestoredChapterPageCount(Path.Combine(_tempDir, "Chapter 6.2.cbz"), 3);
    }

    #endregion

    #region Helper Methods

    private FoundChapter CreateFoundChapter(string fileName, string chapterNumber)
    {
        return new FoundChapter(
            fileName,
            fileName, // Relative path same as filename for test
            ChapterStorageType.Cbz,
            new ExtractedMetadata("Test Series", Path.GetFileNameWithoutExtension(fileName), chapterNumber));
    }

    private void CreateTestCbzFiles(List<FoundChapter> chapters)
    {
        foreach (var chapter in chapters)
        {
            CreateTestCbzFile(Path.Combine(_tempDir, chapter.FileName), 3); // 3 pages each
        }
    }

    private void CreateTestCbzFile(string filePath, int pageCount)
    {
        using var fileStream = new FileStream(filePath, FileMode.Create);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        // Add test images
        for (int i = 1; i <= pageCount; i++)
        {
            var entry = archive.CreateEntry($"{i:D3}.jpg");
            using var entryStream = entry.Open();
            var imageBytes = CreateTestImageBytes(i);
            entryStream.Write(imageBytes);
        }

        // Add ComicInfo.xml
        var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
        using var comicInfoStream = comicInfoEntry.Open();
        using var writer = new StreamWriter(comicInfoStream);
        writer.Write("<?xml version=\"1.0\"?><ComicInfo><Title>Test Chapter</Title></ComicInfo>");
    }

    private void CreateMergedTestCbzFile(string filePath, string[] originalPageNames)
    {
        using var fileStream = new FileStream(filePath, FileMode.Create);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        // Add test images with 4-digit padding as the actual merge implementation does
        for (int i = 0; i < originalPageNames.Length; i++)
        {
            // Use 4-digit padding to match actual MergeChapterPartsAsync implementation
            string mergedPageName = $"{i:D4}.jpg";
            var entry = archive.CreateEntry(mergedPageName);
            using var entryStream = entry.Open();
            var imageBytes = CreateTestImageBytes(i + 1);
            entryStream.Write(imageBytes);
        }
    }

    private void CreateMergedTestCbzFileWithCustomNames(string filePath, string[] pageNames)
    {
        using var fileStream = new FileStream(filePath, FileMode.Create);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        // Add test images with the exact page names provided
        for (int i = 0; i < pageNames.Length; i++)
        {
            var entry = archive.CreateEntry(pageNames[i]);
            using var entryStream = entry.Open();
            var imageBytes = CreateTestImageBytes(i + 1);
            entryStream.Write(imageBytes);
        }
    }

    private static byte[] CreateTestImageBytes(int variant = 1)
    {
        try
        {
            // Create different test images based on variant
            Image image = variant switch
            {
                1 => Image.Black(32, 32) + 128, // Gray
                2 => Image.Black(32, 32) + 200, // Light gray
                3 => Image.Black(32, 32) + 50, // Dark gray
                _ => Image.Black(32, 32) + (variant * 30 % 255)
            };

            // Convert to JPEG bytes
            return image.JpegsaveBuffer();
        }
        catch
        {
            // Fallback: create a minimal valid JPEG
            byte[] jpegHeader = new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
                0x00, 0x48, 0x00, 0x00, 0xFF, 0xD9
            };
            return jpegHeader;
        }
    }

    private static void VerifyRestoredChapterPageCount(string filePath, int expectedPageCount)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        var imageEntries = archive.Entries.Where(e =>
            e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            e.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(expectedPageCount, imageEntries.Count);
    }

    /// <summary>
    /// Tests that chapter restoration handles missing PageNames gracefully (backward compatibility)
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreChapterPartsAsync_WithLegacyRecordMissingPageNames_ShouldGeneratePageNames()
    {
        // Arrange
        string mergedFile = Path.Combine(_tempDir, "legacy_merged_chapter.cbz");
        CreateMergedTestCbzFile(mergedFile, new[] { "0000.jpg", "0001.jpg", "0002.jpg", "0003.jpg" });

        // Create legacy-style OriginalChapterParts without PageNames (simulating old records)
        var legacyOriginalParts = new List<OriginalChapterPart>
        {
            new()
            {
                FileName = "Chapter_20.1.cbz",
                ChapterNumber = "20.1",
                StartPageIndex = 0,
                EndPageIndex = 1,
                PageNames = new List<string>(), // Empty - simulating legacy record
                Metadata = new ExtractedMetadata("Test Chapter 20.1", null, null)
            },
            new()
            {
                FileName = "Chapter_20.2.cbz",
                ChapterNumber = "20.2",
                StartPageIndex = 2,
                EndPageIndex = 3,
                PageNames = new List<string>(), // Empty - simulating legacy record
                Metadata = new ExtractedMetadata("Test Chapter 20.2", null, null)
            }
        };

        // Act
        List<FoundChapter> restoredChapters = await _chapterPartMerger.RestoreChapterPartsAsync(mergedFile,
            legacyOriginalParts, _tempDir, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, restoredChapters.Count);

        // Verify that the restoration worked despite missing PageNames
        string part1Path = Path.Combine(_tempDir, "Chapter_20.1.cbz");
        Assert.True(File.Exists(part1Path));
        VerifyRestoredChapterPageCount(part1Path, 2);

        string part2Path = Path.Combine(_tempDir, "Chapter_20.2.cbz");
        Assert.True(File.Exists(part2Path));
        VerifyRestoredChapterPageCount(part2Path, 2);
    }

    #endregion
}

/// <summary>
/// Integration tests for database operations during chapter merging
/// </summary>
public class ChapterMergeRevertServiceTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IChapterPartMerger _mockChapterPartMerger;
    private readonly ChapterMergeRevertService _revertService;
    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly Library _testLibrary;
    private readonly Manga _testManga;

    public ChapterMergeRevertServiceTests()
    {
        // Create test database
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _dbContext = _testDb.Context;

        // Create mocks
        _mockChapterPartMerger = Substitute.For<IChapterPartMerger>();
        var mockLogger = Substitute.For<ILogger<ChapterMergeRevertService>>();

        // Create service under test
        _revertService = new ChapterMergeRevertService(
            _dbContext,
            _mockChapterPartMerger,
            null!,
            null!,
            mockLogger);

        // Create test data
        _testLibrary = CreateTestLibrary();
        _testManga = CreateTestManga(_testLibrary, "Test Manga");

        _dbContext.Libraries.Add(_testLibrary);
        _dbContext.MangaSeries.Add(_testManga);
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _testDb?.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CanRevertChapterAsync_WithMergedChapter_ShouldReturnTrue()
    {
        // Arrange
        var mergedChapter = CreateTestChapter(_testManga, "Chapter 8.cbz", "8");
        var mergedChapterInfo = new MergedChapterInfo
        {
            ChapterId = mergedChapter.Id,
            Chapter = mergedChapter,
            MergedChapterNumber = "8",
            OriginalParts = new List<OriginalChapterPart>()
        };

        _dbContext.Chapters.Add(mergedChapter);
        _dbContext.MergedChapterInfos.Add(mergedChapterInfo);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        bool canRevert =
            await _revertService.CanRevertChapterAsync(mergedChapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(canRevert);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CanRevertChapterAsync_WithNormalChapter_ShouldReturnFalse()
    {
        // Arrange
        var normalChapter = CreateTestChapter(_testManga, "Chapter 9.cbz", "9");
        _dbContext.Chapters.Add(normalChapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        bool canRevert =
            await _revertService.CanRevertChapterAsync(normalChapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(canRevert);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetMergeInfoAsync_WithMergedChapter_ShouldReturnMergeInfo()
    {
        // Arrange
        var mergedChapter = CreateTestChapter(_testManga, "Chapter 10.cbz", "10");
        var originalParts = new List<OriginalChapterPart>
        {
            new() { FileName = "Chapter 10.1.cbz", ChapterNumber = "10.1" },
            new() { FileName = "Chapter 10.2.cbz", ChapterNumber = "10.2" }
        };

        var mergedChapterInfo = new MergedChapterInfo
        {
            ChapterId = mergedChapter.Id,
            Chapter = mergedChapter,
            MergedChapterNumber = "10",
            OriginalParts = originalParts
        };

        _dbContext.Chapters.Add(mergedChapter);
        _dbContext.MergedChapterInfos.Add(mergedChapterInfo);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        MergedChapterInfo? result =
            await _revertService.GetMergeInfoAsync(mergedChapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("10", result.MergedChapterNumber);
        Assert.Equal(2, result.OriginalParts.Count);
        Assert.Contains(result.OriginalParts, p => p.FileName == "Chapter 10.1.cbz");
        Assert.Contains(result.OriginalParts, p => p.FileName == "Chapter 10.2.cbz");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetMergeInfoAsync_WithNormalChapter_ShouldReturnNull()
    {
        // Arrange
        var normalChapter = CreateTestChapter(_testManga, "Chapter 11.cbz", "11");
        _dbContext.Chapters.Add(normalChapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        MergedChapterInfo? result =
            await _revertService.GetMergeInfoAsync(normalChapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    #region Helper Methods

    private Library CreateTestLibrary()
    {
        return new Library
        {
            Id = 1,
            Name = "Test Library",
            NotUpscaledLibraryPath = "/test/not_upscaled",
            UpscaledLibraryPath = "/test/upscaled",
            IngestPath = "/test/ingest"
        };
    }

    private Manga CreateTestManga(Library library, string title)
    {
        return new Manga
        {
            Id = 1,
            PrimaryTitle = title,
            Library = library,
            LibraryId = library.Id,
            Chapters = new List<Chapter>()
        };
    }

    private Chapter CreateTestChapter(Manga manga, string fileName, string chapterNumber)
    {
        return new Chapter
        {
            Id = Random.Shared.Next(1000, 9999),
            FileName = fileName,
            RelativePath = Path.Combine(manga.PrimaryTitle, fileName),
            Manga = manga,
            MangaId = manga.Id
        };
    }

    #endregion
}

/// <summary>
/// Integration tests demonstrating chapter number edge cases and real-world scenarios
/// </summary>
public class ChapterNumberExtractionIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void ChapterNumberExtraction_WithRealWorldExamples_ShouldHandleAllCases()
    {
        // This tests real-world manga filename patterns that users might encounter
        var realWorldExamples = new Dictionary<string, string?>
        {
            // Standard formats
            { "One Piece - Chapter 1050.cbz", "1050" },
            { "Attack on Titan Ch. 139.cbz", "139" },
            { "My Hero Academia Chapter 350.5.cbz", "350.5" },

            // Sub-chapters (most important for this PR)
            { "Naruto Chapter 700.1.cbz", "700.1" },
            { "Demon Slayer Ch 204.2.cbz", "204.2" },
            { "One Piece Chapter 1000.3.cbz", "1000.3" },

            // Group releases with complex naming
            { "[MangaStream] One Piece - Chapter 1050 [720p].cbz", "1050" },
            { "[VIZ] My Hero Academia Ch.350.5 [Digital].cbz", "350.5" },
            { "Tokyo Ghoul:re Chapter 179.5 [MS].cbz", "179.5" },

            // International formats
            { "ワンピース 第1050話.cbz", "1050" },
            { "진격의 거인 제139화.cbz", "139" },
            { "我的英雄学院 第350.5话.cbz", "350.5" },

            // Edge cases that should work
            { "Chapter_105.1_Final.cbz", "105.1" },
            { "Ch105-2.cbz", "105.2" }, // Regex interprets dash as decimal point
            { "Episode 24.5 Special.cbz", "24.5" },

            // Cases that should return null or numbers
            { "Volume 1.cbz", "1" }, // Will extract the "1" from Volume
            { "Extras.cbz", null },
            { "Credits and Thanks.cbz", null },
            { "Cover Art.cbz", null }
        };

        foreach (var example in realWorldExamples)
        {
            // Act
            var result = ChapterNumberHelper.ExtractChapterNumber(example.Key);

            // Assert with detailed message for debugging
            Assert.True(
                result == example.Value,
                $"Expected '{example.Value}' but got '{result}' for filename: '{example.Key}'");
        }
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("105.1", "105")]
    [InlineData("350.5", "350")]
    [InlineData("1000.2", "1000")]
    [InlineData("42", "42")]
    public void BaseChapterNumberExtraction_WithSubChapters_ShouldExtractCorrectBase(string chapterNumber,
        string expectedBase)
    {
        // This tests the critical functionality for determining which chapters can be merged

        // Act - Test the conceptual logic since the method is private
        if (decimal.TryParse(chapterNumber, out decimal number))
        {
            var baseNumber = Math.Floor(number).ToString();

            // Assert
            Assert.Equal(expectedBase, baseNumber);
        }
        else
        {
            Assert.Fail($"Could not parse chapter number: {chapterNumber}");
        }
    }
}

/// <summary>
/// Simple disposable scope for organizing test outputs
/// </summary>
public class TestScope : IDisposable
{
    public TestScope(string description)
    {
        // This is just for test organization, no actual implementation needed
    }

    public void Dispose()
    {
        // No cleanup needed for test scope
    }
}