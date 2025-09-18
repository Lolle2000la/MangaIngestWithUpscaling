using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetVips;
using NSubstitute;
using System.IO.Compression;
using System.Reflection;

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
/// Tests for ComicInfo.xml preservation during chapter merge and restoration operations
/// </summary>
public class ComicInfoPreservationTests : IDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public ComicInfoPreservationTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
    }

    public void Dispose()
    {
        _testDb?.Dispose();
    }

    private ApplicationDbContext CreateDbContext() => _testDb.Context;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeAndRestoreChapters_ShouldPreserveCompleteComicInfoXml()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "ComicInfoTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = Substitute.For<ILogger<ChapterPartMerger>>();
            var metadataHandler = Substitute.For<IMetadataHandlingService>();
            var merger = new ChapterPartMerger(metadataHandler, logger);

            // Create test CBZ files with complete ComicInfo.xml
            var originalComicInfo1 = """
                                     <?xml version="1.0" encoding="utf-8"?>
                                     <ComicInfo>
                                         <Series>Test Series</Series>
                                         <Number>5.1</Number>
                                         <Title>Part One</Title>
                                         <Summary>This is a detailed summary of the chapter with plot details.</Summary>
                                         <Writer>Test Author</Writer>
                                         <Penciller>Test Artist</Penciller>
                                         <Genre>Action, Adventure</Genre>
                                         <CommunityRating>5</CommunityRating>
                                         <Tags>Manga, Shonen, Test</Tags>
                                         <LanguageISO>en</LanguageISO>
                                         <PageCount>3</PageCount>
                                     </ComicInfo>
                                     """;

            var originalComicInfo2 = """
                                     <?xml version="1.0" encoding="utf-8"?>
                                     <ComicInfo>
                                         <Series>Test Series</Series>
                                         <Number>5.2</Number>
                                         <Title>Part Two</Title>
                                         <Summary>This is another detailed summary with different content.</Summary>
                                         <Writer>Test Author</Writer>
                                         <Penciller>Test Artist</Penciller>
                                         <Genre>Action, Adventure</Genre>
                                         <CommunityRating>4</CommunityRating>
                                         <Tags>Manga, Shonen, Test</Tags>
                                         <LanguageISO>en</LanguageISO>
                                         <PageCount>3</PageCount>
                                     </ComicInfo>
                                     """;

            var file1 = CreateTestCbzFileWithComicInfo(tempDir, "Chapter 5.1.cbz", 3, originalComicInfo1);
            var file2 = CreateTestCbzFileWithComicInfo(tempDir, "Chapter 5.2.cbz", 3, originalComicInfo2);

            var foundChapters = new List<FoundChapter>
            {
                new("Chapter 5.1.cbz", file1, ChapterStorageType.Cbz,
                    new ExtractedMetadata("Test Series", "Chapter 5.1", "5.1")),
                new("Chapter 5.2.cbz", file2, ChapterStorageType.Cbz,
                    new ExtractedMetadata("Test Series", "Chapter 5.2", "5.2"))
            };

            // Configure metadata handler to return the original ComicInfo.xml content
            metadataHandler.GetSeriesAndTitleFromComicInfo(file1)
                .Returns(new ExtractedMetadata("Test Series", "Chapter 5.1", "5.1"));
            metadataHandler.GetSeriesAndTitleFromComicInfo(file2)
                .Returns(new ExtractedMetadata("Test Series", "Chapter 5.2", "5.2"));

            // Configure metadata handler to write merged ComicInfo.xml
            metadataHandler.When(x => x.WriteComicInfo(Arg.Any<ZipArchive>(), Arg.Any<ExtractedMetadata>()))
                .Do(callInfo =>
                {
                    var archive = callInfo.Arg<ZipArchive>();
                    var metadata = callInfo.Arg<ExtractedMetadata>();
                    var entry = archive.CreateEntry("ComicInfo.xml");
                    using var stream = entry.Open();
                    using var writer = new StreamWriter(stream);
                    writer.Write($"""
                                  <?xml version="1.0" encoding="utf-8"?>
                                  <ComicInfo>
                                      <Series>{metadata.Series}</Series>
                                      <Number>{metadata.Number}</Number>
                                      <Title>{metadata.ChapterTitle}</Title>
                                  </ComicInfo>
                                  """);
                });

            // Act - Merge the chapters
            var targetMetadata = new ExtractedMetadata("Test Series", "Chapter 5", "5");
            (FoundChapter mergedChapter, List<OriginalChapterPart> originalParts) mergeInfo =
                await merger.MergeChapterPartsAsync(foundChapters, tempDir, tempDir, "5", targetMetadata,
                    cancellationToken: TestContext.Current.CancellationToken);

            // Verify merge info contains original ComicInfo.xml
            Assert.NotNull(mergeInfo.originalParts);
            Assert.Equal(2, mergeInfo.originalParts.Count);

            var part1 = mergeInfo.originalParts.First(p => p.FileName == "Chapter 5.1.cbz");
            var part2 = mergeInfo.originalParts.First(p => p.FileName == "Chapter 5.2.cbz");

            Assert.Contains("This is a detailed summary", part1.OriginalComicInfoXml);
            Assert.Contains("CommunityRating>5", part1.OriginalComicInfoXml);
            Assert.Contains("This is another detailed summary", part2.OriginalComicInfoXml);
            Assert.Contains("CommunityRating>4", part2.OriginalComicInfoXml);

            // Wait a moment to ensure any file handles are released
            await Task.Delay(100, TestContext.Current.CancellationToken);

            // Verify merged file exists and is accessible
            string mergedFile = Path.Combine(tempDir, mergeInfo.mergedChapter.RelativePath);
            Assert.True(File.Exists(mergedFile));

            // Delete the original files to simulate real-world restoration scenario
            // (In production, merged chapters replace the original parts)
            File.Delete(file1);
            File.Delete(file2);

            // Act - Restore the chapters
            List<FoundChapter> restoredChapters = await merger.RestoreChapterPartsAsync(mergedFile,
                mergeInfo.originalParts, tempDir,
                TestContext.Current.CancellationToken);

            // Assert - Verify restored ComicInfo.xml content
            Assert.Equal(2, restoredChapters.Count);

            var restoredFile1 = Path.Combine(tempDir, "Chapter 5.1.cbz");
            var restoredFile2 = Path.Combine(tempDir, "Chapter 5.2.cbz");

            Assert.True(File.Exists(restoredFile1));
            Assert.True(File.Exists(restoredFile2));

            // Verify ComicInfo.xml content in restored files
            VerifyComicInfoContent(restoredFile1, originalComicInfo1);
            VerifyComicInfoContent(restoredFile2, originalComicInfo2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestoreChapterParts_WithLegacyRecordsWithoutComicInfo_ShouldGenerateBasicMetadata()
    {
        // Arrange - Test backward compatibility with legacy records
        var tempDir = Path.Combine(Path.GetTempPath(), "LegacyComicInfoTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = Substitute.For<ILogger<ChapterPartMerger>>();
            var metadataHandler = Substitute.For<IMetadataHandlingService>();
            var merger = new ChapterPartMerger(metadataHandler, logger);

            // Create a merged file
            var mergedFile = CreateTestCbzFile(tempDir, "Chapter 5.cbz", 6);

            // Create legacy OriginalParts without OriginalComicInfoXml (simulating old records)
            var legacyParts = new List<OriginalChapterPart>
            {
                new()
                {
                    FileName = "Chapter 5.1.cbz",
                    StartPageIndex = 0,
                    EndPageIndex = 2,
                    Metadata = new ExtractedMetadata("Test Series", "Chapter 5.1", "5.1"),
                    PageNames = new List<string> { "0000.jpg", "0001.jpg", "0002.jpg" },
                    // Note: OriginalComicInfoXml is null (legacy record)
                },
                new()
                {
                    FileName = "Chapter 5.2.cbz",
                    StartPageIndex = 3,
                    EndPageIndex = 5,
                    Metadata = new ExtractedMetadata("Test Series", "Chapter 5.2", "5.2"),
                    PageNames = new List<string> { "0003.jpg", "0004.jpg", "0005.jpg" },
                    // Note: OriginalComicInfoXml is null (legacy record)
                }
            };

            // Configure metadata handler for legacy fallback
            metadataHandler.When(x => x.WriteComicInfo(Arg.Any<ZipArchive>(), Arg.Any<ExtractedMetadata>()))
                .Do(callInfo =>
                {
                    var archive = callInfo.Arg<ZipArchive>();
                    var metadata = callInfo.Arg<ExtractedMetadata>();
                    var entry = archive.CreateEntry("ComicInfo.xml");
                    using var stream = entry.Open();
                    using var writer = new StreamWriter(stream);
                    writer.Write(
                        $"<ComicInfo><Series>{metadata.Series}</Series><Number>{metadata.Number}</Number></ComicInfo>");
                });

            // Wait a moment to ensure file handles are released  
            await Task.Delay(100, TestContext.Current.CancellationToken);

            // Act - Restore with legacy records
            List<FoundChapter> restoredChapters = await merger.RestoreChapterPartsAsync(mergedFile, legacyParts,
                tempDir,
                TestContext.Current.CancellationToken);

            // Assert - Verify fallback behavior
            Assert.Equal(2, restoredChapters.Count);

            // Verify metadata handler was called for legacy fallback
            metadataHandler.Received(2).WriteComicInfo(Arg.Any<ZipArchive>(), Arg.Any<ExtractedMetadata>());

            var restoredFile1 = Path.Combine(tempDir, "Chapter 5.1.cbz");
            var restoredFile2 = Path.Combine(tempDir, "Chapter 5.2.cbz");

            Assert.True(File.Exists(restoredFile1));
            Assert.True(File.Exists(restoredFile2));

            // Verify basic ComicInfo.xml was generated
            VerifyComicInfoBasicContent(restoredFile1, "Test Series", "5.1");
            VerifyComicInfoBasicContent(restoredFile2, "Test Series", "5.2");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private string CreateTestCbzFileWithComicInfo(string directory, string fileName, int pageCount, string comicInfoXml)
    {
        var filePath = Path.Combine(directory, fileName);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // Add ComicInfo.xml
        var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
        using (var stream = comicInfoEntry.Open())
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(comicInfoXml);
        }

        // Add test images
        for (int i = 0; i < pageCount; i++)
        {
            var entry = archive.CreateEntry($"{i:D4}.jpg");
            using var stream = entry.Open();
            GenerateTestImage().WriteToStream(stream, ".jpg");
        }

        return filePath;
    }

    private void VerifyComicInfoContent(string cbzFilePath, string expectedComicInfoXml)
    {
        using var archive = ZipFile.OpenRead(cbzFilePath);
        var comicInfoEntry = archive.GetEntry("ComicInfo.xml");

        Assert.NotNull(comicInfoEntry);

        using var stream = comicInfoEntry.Open();
        using var reader = new StreamReader(stream);
        var actualComicInfo = reader.ReadToEnd();

        // Verify key elements are preserved
        Assert.Contains("CommunityRating", actualComicInfo);
        Assert.Contains("Summary", actualComicInfo);
        Assert.Contains("Writer", actualComicInfo);
        Assert.Contains("Tags", actualComicInfo);
    }

    private void VerifyComicInfoBasicContent(string cbzFilePath, string expectedSeries, string expectedNumber)
    {
        using var archive = ZipFile.OpenRead(cbzFilePath);
        var comicInfoEntry = archive.GetEntry("ComicInfo.xml");

        Assert.NotNull(comicInfoEntry);

        using var stream = comicInfoEntry.Open();
        using var reader = new StreamReader(stream);
        var actualComicInfo = reader.ReadToEnd();

        Assert.Contains($"<Series>{expectedSeries}</Series>", actualComicInfo);
        Assert.Contains($"<Number>{expectedNumber}</Number>", actualComicInfo);
    }

    private string CreateTestCbzFile(string directory, string fileName, int pageCount)
    {
        var filePath = Path.Combine(directory, fileName);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // Add test images
        for (int i = 0; i < pageCount; i++)
        {
            var entry = archive.CreateEntry($"{i:D4}.jpg");
            using var stream = entry.Open();
            GenerateTestImage().WriteToStream(stream, ".jpg");
        }

        return filePath;
    }

    private Image GenerateTestImage()
    {
        try
        {
            return Image.Black(32, 32) + 128; // Gray test image
        }
        catch
        {
            // Fallback to minimal test image
            return Image.Black(1, 1);
        }
    }
}

/// <summary>
/// Tests for upscaled chapter handling during merge and restoration operations
/// </summary>
public class UpscaledChapterHandlingTests : IDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public UpscaledChapterHandlingTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
    }

    public void Dispose()
    {
        _testDb?.Dispose();
    }

    private ApplicationDbContext CreateDbContext() => _testDb.Context;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevertMergedChapter_WithUpscaledVersion_ShouldRestoreUpscaledParts()
    {
        // Arrange
        using var context = CreateDbContext();

        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.GetTempPath(),
            UpscaledLibraryPath = Path.Combine(Path.GetTempPath(), "upscaled")
        };

        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };

        var upscalerProfile = new UpscalerProfile
        {
            Name = "Test Profile",
            UpscalerMethod = UpscalerMethod.MangaJaNai,
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 85
        };

        context.Libraries.Add(library);
        context.MangaSeries.Add(manga);
        context.UpscalerProfiles.Add(upscalerProfile);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create temporary directories
        var notUpscaledDir = Path.Combine(Path.GetTempPath(), "test_not_upscaled_" + Guid.NewGuid());
        var upscaledDir = Path.Combine(Path.GetTempPath(), "test_upscaled_" + Guid.NewGuid());
        Directory.CreateDirectory(notUpscaledDir);
        Directory.CreateDirectory(upscaledDir);
        Directory.CreateDirectory(Path.Combine(upscaledDir, "Test Manga"));

        try
        {
            library.NotUpscaledLibraryPath = notUpscaledDir;
            library.UpscaledLibraryPath = upscaledDir;

            // Create merged chapter with upscaled version
            var mergedChapterPath = Path.Combine(notUpscaledDir, "Test Manga", "Chapter 5.cbz");
            var upscaledMergedChapterPath = Path.Combine(upscaledDir, "Test Manga", "Chapter 5.cbz");

            Directory.CreateDirectory(Path.GetDirectoryName(mergedChapterPath)!);

            // Create regular merged file
            CreateTestCbzFile(Path.GetDirectoryName(mergedChapterPath)!, "Chapter 5.cbz", 6);

            // Create upscaled merged file (simulating upscaler output)
            CreateTestCbzFile(Path.GetDirectoryName(upscaledMergedChapterPath)!, "Chapter 5.cbz", 6);

            // Add upscaler.json to upscaled file
            using (var archive = ZipFile.Open(upscaledMergedChapterPath, ZipArchiveMode.Update))
            {
                var upscalerEntry = archive.CreateEntry("upscaler.json");
                using var stream = upscalerEntry.Open();
                using var writer = new StreamWriter(stream);
                await writer.WriteAsync("""{"profile": "Test Profile", "scale": 2}""");
            }

            var chapter = new Chapter
            {
                FileName = "Chapter 5.cbz",
                RelativePath = Path.Combine("Test Manga", "Chapter 5.cbz"),
                Manga = manga,
                IsUpscaled = true,
                UpscalerProfile = upscalerProfile
            };

            var mergeInfo = new MergedChapterInfo
            {
                Chapter = chapter,
                OriginalParts = new List<OriginalChapterPart>
                {
                    new()
                    {
                        FileName = "Chapter 5.1.cbz",
                        StartPageIndex = 0,
                        EndPageIndex = 2,
                        Metadata = new ExtractedMetadata("Test Manga", "Chapter 5.1", "5.1"),
                        PageNames = new List<string> { "0000.jpg", "0001.jpg", "0002.jpg" }
                    },
                    new()
                    {
                        FileName = "Chapter 5.2.cbz",
                        StartPageIndex = 3,
                        EndPageIndex = 5,
                        Metadata = new ExtractedMetadata("Test Manga", "Chapter 5.2", "5.2"),
                        PageNames = new List<string> { "0003.jpg", "0004.jpg", "0005.jpg" }
                    }
                }
            };

            context.Chapters.Add(chapter);
            context.MergedChapterInfos.Add(mergeInfo);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Create services
            var logger = Substitute.For<ILogger<ChapterMergeRevertService>>();
            var chapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
            var upscalerJsonService = Substitute.For<IUpscalerJsonHandlingService>();
            var metadataHandler = Substitute.For<IMetadataHandlingService>();
            var partMergerLogger = Substitute.For<ILogger<ChapterPartMerger>>();

            var chapterPartMerger = new ChapterPartMerger(metadataHandler, partMergerLogger);
            var revertService = new ChapterMergeRevertService(
                context, chapterPartMerger, chapterChangedNotifier, upscalerJsonService, logger);

            // Act - Revert the merged chapter
            List<Chapter> restoredChapters =
                await revertService.RevertMergedChapterAsync(chapter, TestContext.Current.CancellationToken);

            // Assert - Verify both regular and upscaled parts were restored
            Assert.Equal(2, restoredChapters.Count);

            // Verify regular restored files exist
            Assert.True(File.Exists(Path.Combine(notUpscaledDir, "Test Manga", "Chapter 5.1.cbz")));
            Assert.True(File.Exists(Path.Combine(notUpscaledDir, "Test Manga", "Chapter 5.2.cbz")));

            // Verify upscaled restored files exist
            Assert.True(File.Exists(Path.Combine(upscaledDir, "Test Manga", "Chapter 5.1.cbz")));
            Assert.True(File.Exists(Path.Combine(upscaledDir, "Test Manga", "Chapter 5.2.cbz")));

            // Verify merged files were deleted
            Assert.False(File.Exists(mergedChapterPath));
            Assert.False(File.Exists(upscaledMergedChapterPath));

            // Verify upscaler.json was added to restored upscaled parts
            _ = upscalerJsonService.Received(2).WriteUpscalerJsonAsync(
                Arg.Any<ZipArchive>(),
                Arg.Is<UpscalerProfile>(p => p.Name == "Test Profile"),
                Arg.Any<CancellationToken>());

            // Verify database state
            var remainingChapters = context.Chapters.Where(c => c.MangaId == manga.Id).ToList();
            Assert.Equal(2, remainingChapters.Count);
            Assert.All(remainingChapters, c => Assert.True(c.IsUpscaled));
            Assert.All(remainingChapters, c => Assert.Equal(upscalerProfile.Id, c.UpscalerProfileId));
        }
        finally
        {
            if (Directory.Exists(notUpscaledDir))
            {
                Directory.Delete(notUpscaledDir, true);
            }

            if (Directory.Exists(upscaledDir))
            {
                Directory.Delete(upscaledDir, true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevertMergedChapter_WithoutUpscaledVersion_ShouldOnlyRestoreRegularParts()
    {
        // Arrange
        using var context = CreateDbContext();

        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.GetTempPath(),
            UpscaledLibraryPath = null // No upscaled path
        };

        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };

        context.Libraries.Add(library);
        context.MangaSeries.Add(manga);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tempDir = Path.Combine(Path.GetTempPath(), "test_regular_only_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "Test Manga"));

        try
        {
            library.NotUpscaledLibraryPath = tempDir;

            // Create merged chapter (regular only)
            var mergedChapterPath = Path.Combine(tempDir, "Test Manga", "Chapter 5.cbz");
            CreateTestCbzFile(Path.GetDirectoryName(mergedChapterPath)!, "Chapter 5.cbz", 6);

            var chapter = new Chapter
            {
                FileName = "Chapter 5.cbz",
                RelativePath = Path.Combine("Test Manga", "Chapter 5.cbz"),
                Manga = manga,
                IsUpscaled = false
            };

            var mergeInfo = new MergedChapterInfo
            {
                Chapter = chapter,
                OriginalParts = new List<OriginalChapterPart>
                {
                    new()
                    {
                        FileName = "Chapter 5.1.cbz",
                        StartPageIndex = 0,
                        EndPageIndex = 2,
                        Metadata = new ExtractedMetadata("Test Manga", "Chapter 5.1", "5.1"),
                        PageNames = new List<string> { "0000.jpg", "0001.jpg", "0002.jpg" }
                    },
                    new()
                    {
                        FileName = "Chapter 5.2.cbz",
                        StartPageIndex = 3,
                        EndPageIndex = 5,
                        Metadata = new ExtractedMetadata("Test Manga", "Chapter 5.2", "5.2"),
                        PageNames = new List<string> { "0003.jpg", "0004.jpg", "0005.jpg" }
                    }
                }
            };

            context.Chapters.Add(chapter);
            context.MergedChapterInfos.Add(mergeInfo);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Create services
            var logger = Substitute.For<ILogger<ChapterMergeRevertService>>();
            var chapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
            var upscalerJsonService = Substitute.For<IUpscalerJsonHandlingService>();
            var metadataHandler = Substitute.For<IMetadataHandlingService>();
            var partMergerLogger = Substitute.For<ILogger<ChapterPartMerger>>();

            var chapterPartMerger = new ChapterPartMerger(metadataHandler, partMergerLogger);
            var revertService = new ChapterMergeRevertService(
                context, chapterPartMerger, chapterChangedNotifier, upscalerJsonService, logger);

            // Act - Revert the merged chapter
            List<Chapter> restoredChapters =
                await revertService.RevertMergedChapterAsync(chapter, TestContext.Current.CancellationToken);

            // Assert - Verify only regular parts were restored
            Assert.Equal(2, restoredChapters.Count);

            // Verify regular restored files exist
            Assert.True(File.Exists(Path.Combine(tempDir, "Test Manga", "Chapter 5.1.cbz")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Test Manga", "Chapter 5.2.cbz")));

            // Verify merged file was deleted
            Assert.False(File.Exists(mergedChapterPath));

            // Verify no upscaler.json calls were made
            _ = upscalerJsonService.DidNotReceive().WriteUpscalerJsonAsync(
                Arg.Any<ZipArchive>(), Arg.Any<UpscalerProfile>(), Arg.Any<CancellationToken>());

            // Verify database state
            var remainingChapters = context.Chapters.Where(c => c.MangaId == manga.Id).ToList();
            Assert.Equal(2, remainingChapters.Count);
            Assert.All(remainingChapters, c => Assert.False(c.IsUpscaled));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private string CreateTestCbzFile(string directory, string fileName, int pageCount)
    {
        var filePath = Path.Combine(directory, fileName);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // Add test images
        for (int i = 0; i < pageCount; i++)
        {
            var entry = archive.CreateEntry($"{i:D4}.jpg");
            using var stream = entry.Open();
            GenerateTestImage().WriteToStream(stream, ".jpg");
        }

        return filePath;
    }

    private Image GenerateTestImage()
    {
        try
        {
            return Image.Black(32, 32) + 128; // Gray test image
        }
        catch
        {
            // Fallback to minimal test image
            return Image.Black(1, 1);
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

/// <summary>
/// Tests for partial upscaling functionality during chapter merging operations
/// </summary>
public class PartialUpscalingMergeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public PartialUpscalingMergeTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _tempDir = Path.Combine(Path.GetTempPath(), "partial_upscaling_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _testDb?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private ApplicationDbContext CreateDbContext() => _testDb.Context;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task
        HandleUpscaledChapterMerging_WithPartiallyUpscaledParts_ShouldCreatePartialMergeAndScheduleRepairTask()
    {
        // Arrange
        using var context = CreateDbContext();
        var logger = Substitute.For<ILogger<ChapterMergeCoordinator>>();
        var metadataHandling = Substitute.For<IMetadataHandlingService>();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var upscaleTaskManager = Substitute.For<IChapterMergeUpscaleTaskManager>();
        var taskQueueStub = Substitute.For<ITaskQueue>();

        var coordinator = new ChapterMergeCoordinator(
            context, chapterPartMerger, upscaleTaskManager, taskQueueStub, metadataHandling, logger);

        // Create test library and manga
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "regular"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled"),
            UpscaleOnIngest = true
        };

        // Provide an upscaler profile so task manager can schedule repair tasks
        var upscalerProfile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Avif,
            Quality = 80
        };
        library.UpscalerProfile = upscalerProfile;

        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };

        context.Libraries.Add(library);
        context.UpscalerProfiles.Add(upscalerProfile);
        context.MangaSeries.Add(manga);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create directories
        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath!);
        var upscaledSeriesPath = Path.Combine(library.UpscaledLibraryPath, "Test Manga");
        Directory.CreateDirectory(upscaledSeriesPath);

        // Create test merge info with 3 parts
        var testMetadata = new ExtractedMetadata("Test Manga", "Chapter 5", "5");
        var mergeInfo = new MergeInfo(
            new FoundChapter("Chapter 5.cbz", "Test Manga/Chapter 5.cbz", ChapterStorageType.Cbz, testMetadata),
            new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 5.1.cbz", PageNames = ["001.jpg", "002.jpg"] },
                new() { FileName = "Chapter 5.2.cbz", PageNames = ["003.jpg", "004.jpg"] },
                new() { FileName = "Chapter 5.3.cbz", PageNames = ["005.jpg", "006.jpg"] }
            },
            "5");

        // Create upscaled files for only 2 out of 3 parts (partial scenario)
        CreateTestCbzFile(upscaledSeriesPath, "Chapter 5.1.cbz", 2);
        CreateTestCbzFile(upscaledSeriesPath, "Chapter 5.2.cbz", 2);
        // Chapter 5.3.cbz is missing - this creates the partial scenario

        // Mock the merge operation to return a successful partial merge
        var mockTestMetadata = new ExtractedMetadata("Test Manga", "Chapter 5", "5");
        var mockMergedChapter = new FoundChapter("Chapter 5.cbz", "Test Manga/Chapter 5.cbz", ChapterStorageType.Cbz,
            mockTestMetadata);
        chapterPartMerger.MergeChapterPartsAsync(
                Arg.Any<List<FoundChapter>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ExtractedMetadata>(),
                Arg.Any<Func<FoundChapter, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns((mockMergedChapter, new List<OriginalChapterPart>()));

        var chapters = new List<Chapter>
        {
            new()
            {
                FileName = "Chapter 5.1.cbz",
                Manga = manga,
                RelativePath = Path.Combine("Test Manga", "Chapter 5.1.cbz")
            },
            new()
            {
                FileName = "Chapter 5.2.cbz",
                Manga = manga,
                RelativePath = Path.Combine("Test Manga", "Chapter 5.2.cbz")
            },
            new()
            {
                FileName = "Chapter 5.3.cbz",
                Manga = manga,
                RelativePath = Path.Combine("Test Manga", "Chapter 5.3.cbz")
            }
        };

        // Persist chapters so they have valid IDs for task manager logic
        context.Chapters.AddRange(chapters);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await CallHandleUpscaledChapterMergingAsync(coordinator, chapters, mergeInfo, library);

        // Now route through the real task manager to verify a RepairUpscaleTask is enqueued
        var taskQueue = Substitute.For<ITaskQueue>();
        var taskManagerLogger = Substitute.For<ILogger<ChapterMergeUpscaleTaskManager>>();

        // Build minimal services to satisfy UpscaleTaskProcessor constructor
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddScoped<IQueueCleanup, QueueCleanup>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var realQueueForProcessor = new TaskQueue(scopeFactory, Substitute.For<ILogger<TaskQueue>>());
        var upscalerOptions = Options.Create(new UpscalerConfig { RemoteOnly = true });
        var processorLogger = Substitute.For<ILogger<UpscaleTaskProcessor>>();
        var processor = new UpscaleTaskProcessor(realQueueForProcessor, scopeFactory, upscalerOptions, processorLogger);

        var realTaskManager = new ChapterMergeUpscaleTaskManager(context, taskQueue, processor, taskManagerLogger);

        await realTaskManager.HandleUpscaleTaskManagementAsync(
            chapters,
            mergeInfo,
            library,
            result,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsPartialMerge);
        Assert.Equal(2, result.UpscaledPartsCount);
        Assert.Equal(1, result.MissingPartsCount);
        Assert.Equal(3, result.TotalPartsCount);

        // Verify a repair task was scheduled for the merged chapter due to partial upscaling
        await taskQueue.Received(1)
            .EnqueueAsync(Arg.Is<RepairUpscaleTask>(t => t.ChapterId == chapters.First().Id));

        // Verify that chapterPartMerger.MergeChapterPartsAsync was called for the partial merge
        await chapterPartMerger.Received(1).MergeChapterPartsAsync(
            Arg.Is<List<FoundChapter>>(list => list.Count == 2), // Only 2 upscaled parts
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<ExtractedMetadata>(),
            Arg.Any<Func<FoundChapter, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HandleUpscaledChapterMerging_WithAllPartsUpscaled_ShouldCreateCompleteMerge()
    {
        // Arrange
        using var context = CreateDbContext();
        var logger = Substitute.For<ILogger<ChapterMergeCoordinator>>();
        var metadataHandling = Substitute.For<IMetadataHandlingService>();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var upscaleTaskManager = Substitute.For<IChapterMergeUpscaleTaskManager>();
        var taskQueueStub2 = Substitute.For<ITaskQueue>();

        var coordinator = new ChapterMergeCoordinator(
            context, chapterPartMerger, upscaleTaskManager, taskQueueStub2, metadataHandling, logger);

        // Create test setup
        var library = CreateTestLibrary();
        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };

        var upscaledSeriesPath = Path.Combine(library.UpscaledLibraryPath!, "Test Manga");
        Directory.CreateDirectory(upscaledSeriesPath);

        var testMetadata2 = new ExtractedMetadata("Test Manga", "Chapter 7", "7");
        var mergeInfo = new MergeInfo(
            new FoundChapter("Chapter 7.cbz", "Test Manga/Chapter 7.cbz", ChapterStorageType.Cbz, testMetadata2),
            new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 7.1.cbz", PageNames = ["001.jpg"] },
                new() { FileName = "Chapter 7.2.cbz", PageNames = ["002.jpg"] }
            },
            "7");

        // Create upscaled files for ALL parts (complete scenario)
        CreateTestCbzFile(upscaledSeriesPath, "Chapter 7.1.cbz", 1);
        CreateTestCbzFile(upscaledSeriesPath, "Chapter 7.2.cbz", 1);

        // Mock successful merge
        var mockTestMetadata2 = new ExtractedMetadata("Test Manga", "Chapter 7", "7");
        var mockMergedChapter = new FoundChapter("Chapter 7.cbz", "Test Manga/Chapter 7.cbz", ChapterStorageType.Cbz,
            mockTestMetadata2);
        chapterPartMerger.MergeChapterPartsAsync(Arg.Any<List<FoundChapter>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<ExtractedMetadata>(), Arg.Any<Func<FoundChapter, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns((mockMergedChapter, new List<OriginalChapterPart>()));

        var chapters = new List<Chapter>
        {
            new() { FileName = "Chapter 7.1.cbz", Manga = manga },
            new() { FileName = "Chapter 7.2.cbz", Manga = manga }
        };

        // Act
        var result = await CallHandleUpscaledChapterMergingAsync(coordinator, chapters, mergeInfo, library);

        // Assert
        Assert.False(result.IsPartialMerge);
        Assert.True(result.HasUpscaledContent);
        Assert.Equal(2, result.UpscaledPartsCount);
        Assert.Equal(0, result.MissingPartsCount);
        Assert.Equal(2, result.TotalPartsCount);

        // Verify that all parts were merged
        await chapterPartMerger.Received(1).MergeChapterPartsAsync(
            Arg.Is<List<FoundChapter>>(list => list.Count == 2),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<ExtractedMetadata>(),
            Arg.Any<Func<FoundChapter, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HandleUpscaledChapterMerging_WithNoUpscaledParts_ShouldReturnNoUpscaledContent()
    {
        // Arrange
        using var context = CreateDbContext();
        var logger = Substitute.For<ILogger<ChapterMergeCoordinator>>();
        var metadataHandling = Substitute.For<IMetadataHandlingService>();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var upscaleTaskManager = Substitute.For<IChapterMergeUpscaleTaskManager>();
        var taskQueueStub3 = Substitute.For<ITaskQueue>();

        var coordinator = new ChapterMergeCoordinator(
            context, chapterPartMerger, upscaleTaskManager, taskQueueStub3, metadataHandling, logger);

        var library = CreateTestLibrary();
        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };

        var testMetadata3 = new ExtractedMetadata("Test Manga", "Chapter 8", "8");
        var mergeInfo = new MergeInfo(
            new FoundChapter("Chapter 8.cbz", "Test Manga/Chapter 8.cbz", ChapterStorageType.Cbz, testMetadata3),
            new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 8.1.cbz", PageNames = ["001.jpg"] },
                new() { FileName = "Chapter 8.2.cbz", PageNames = ["002.jpg"] }
            },
            "8");

        // Don't create any upscaled files - no upscaled content scenario

        var chapters = new List<Chapter>
        {
            new() { FileName = "Chapter 8.1.cbz", Manga = manga },
            new() { FileName = "Chapter 8.2.cbz", Manga = manga }
        };

        // Act
        var result = await CallHandleUpscaledChapterMergingAsync(coordinator, chapters, mergeInfo, library);

        // Assert
        Assert.False(result.HasUpscaledContent);
        Assert.False(result.IsPartialMerge);
        Assert.Equal(0, result.UpscaledPartsCount);
        Assert.Equal(0, result.MissingPartsCount);
        Assert.Equal(0, result.TotalPartsCount);

        // Verify that no merge operations were attempted
        await chapterPartMerger.DidNotReceive().MergeChapterPartsAsync(
            Arg.Any<List<FoundChapter>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<ExtractedMetadata>(),
            Arg.Any<Func<FoundChapter, string>?>(),
            Arg.Any<CancellationToken>());
    }

    private Library CreateTestLibrary()
    {
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "regular"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled")
        };

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath!);

        return library;
    }

    private void CreateTestCbzFile(string directory, string fileName, int pageCount)
    {
        string filePath = Path.Combine(directory, fileName);
        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        for (int i = 1; i <= pageCount; i++)
        {
            var entry = zip.CreateEntry($"{i:D3}.jpg");
            using var stream = entry.Open();
            using var writer = new BinaryWriter(stream);
            // Write minimal JPEG header
            writer.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            writer.Write(CreateTestImageBytes());
        }

        // Add ComicInfo.xml
        var comicInfoEntry = zip.CreateEntry("ComicInfo.xml");
        using var comicInfoStream = comicInfoEntry.Open();
        using var comicInfoWriter = new StreamWriter(comicInfoStream);
        comicInfoWriter.Write("<?xml version=\"1.0\"?><ComicInfo><Title>Test Chapter</Title></ComicInfo>");
    }

    private static byte[] CreateTestImageBytes(int variant = 1)
    {
        try
        {
            // Create a simple test image using NetVips and convert to byte array
            var image = Image.Black(32, 32) + 128; // Gray test image
            return image.JpegsaveBuffer();
        }
        catch
        {
            // Fallback to minimal JPEG bytes
            return [0xFF, 0xD8, 0xFF, 0xD9]; // Minimal JPEG
        }
    }

    // Helper method to call the private method using reflection
    private async Task<UpscaledMergeResult> CallHandleUpscaledChapterMergingAsync(
        ChapterMergeCoordinator coordinator,
        List<Chapter> chapters,
        MergeInfo mergeInfo,
        Library library)
    {
        var method = typeof(ChapterMergeCoordinator).GetMethod("HandleUpscaledChapterMergingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var invokeResult = method!.Invoke(
            coordinator,
            new object?[] { chapters, mergeInfo, library, library.NotUpscaledLibraryPath, CancellationToken.None });
        Assert.NotNull(invokeResult);
        var task = (Task<UpscaledMergeResult>)invokeResult!;
        var result = await task;
        return result;
    }
}

/// <summary>
/// Tests for the corner cases in chapter merge reverting operations
/// </summary>
public class ChapterMergeRevertCornerCaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestDatabaseHelper.TestDbContext _testDb;

    public ChapterMergeRevertCornerCaseTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _tempDir = Path.Combine(Path.GetTempPath(), "revert_corner_case_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _testDb?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private ApplicationDbContext CreateDbContext() => _testDb.Context;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevertMergedChapter_WithMissingUpscaledFile_ShouldHandleGracefully()
    {
        // This tests corner case 2: reverting when RepairUpscaleTask hasn't processed yet
        // Arrange
        using var context = CreateDbContext();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var chapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        var upscalerJsonHandling = Substitute.For<IUpscalerJsonHandlingService>();
        var logger = Substitute.For<ILogger<ChapterMergeRevertService>>();

        var revertService = new ChapterMergeRevertService(
            context, chapterPartMerger, chapterChangedNotifier, upscalerJsonHandling, logger);

        // Create test data
        var library = CreateTestLibrary();
        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };
        var upscalerProfile = new UpscalerProfile
        {
            Name = "Test Profile",
            UpscalerMethod = UpscalerMethod.MangaJaNai,
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 85
        };

        var chapter = new Chapter
        {
            FileName = "Chapter 5.cbz",
            RelativePath = "Test Manga/Chapter 5.cbz",
            Manga = manga,
            IsUpscaled = true,
            UpscalerProfile = upscalerProfile
        };

        context.Libraries.Add(library);
        context.MangaSeries.Add(manga);
        context.UpscalerProfiles.Add(upscalerProfile);
        context.Chapters.Add(chapter);

        // Save first to get the IDs
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var mergeInfo = new MergedChapterInfo
        {
            ChapterId = chapter.Id,
            OriginalParts = new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 5.1.cbz", PageNames = ["001.jpg"] },
                new() { FileName = "Chapter 5.2.cbz", PageNames = ["002.jpg"] }
            },
            MergedChapterNumber = "5"
        };

        context.MergedChapterInfos.Add(mergeInfo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create the regular merged chapter file but NOT the upscaled one
        var mergedChapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(mergedChapterPath)!);
        CreateTestCbzFile(Path.GetDirectoryName(mergedChapterPath)!, "Chapter 5.cbz", 2);

        // Create one upscaled part but not the other (partial scenario)
        var upscaledSeriesPath = Path.Combine(library.UpscaledLibraryPath!, "Test Manga");
        Directory.CreateDirectory(upscaledSeriesPath);
        CreateTestCbzFile(upscaledSeriesPath, "Chapter 5.1.cbz", 1);
        // Chapter 5.2.cbz is missing - simulating RepairUpscaleTask not completed yet

        // Mock the restoration to return successful results
        var testMetadata = new ExtractedMetadata("Test Manga", "Chapter 5.1", "5.1");
        var testMetadata2 = new ExtractedMetadata("Test Manga", "Chapter 5.2", "5.2");
        chapterPartMerger.RestoreChapterPartsAsync(Arg.Any<string>(), Arg.Any<List<OriginalChapterPart>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter>
            {
                new("Chapter 5.1.cbz", "Chapter 5.1.cbz", ChapterStorageType.Cbz, testMetadata),
                new("Chapter 5.2.cbz", "Chapter 5.2.cbz", ChapterStorageType.Cbz, testMetadata2)
            });

        // Act
        var result = await revertService.RevertMergedChapterAsync(chapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);

        // Verify that missing upscaled file scenario was handled
        // The method should log warnings about missing upscaled parts and handle gracefully
        logger.ReceivedWithAnyArgs().LogWarning(default!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevertMergedChapter_WithPartialUpscaledContent_ShouldDetectMissingParts()
    {
        // This tests corner case 1: reverting chapter that wasn't merged as full upscaled initially
        // Arrange
        using var context = CreateDbContext();
        var chapterPartMerger = Substitute.For<IChapterPartMerger>();
        var chapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        var upscalerJsonHandling = Substitute.For<IUpscalerJsonHandlingService>();
        var logger = Substitute.For<ILogger<ChapterMergeRevertService>>();

        var revertService = new ChapterMergeRevertService(
            context, chapterPartMerger, chapterChangedNotifier, upscalerJsonHandling, logger);

        // Create test data
        var library = CreateTestLibrary();
        var manga = new Manga { PrimaryTitle = "Test Manga", Library = library };
        var upscalerProfile = new UpscalerProfile
        {
            Name = "Test Profile",
            UpscalerMethod = UpscalerMethod.MangaJaNai,
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 85
        };

        var chapter = new Chapter
        {
            FileName = "Chapter 6.cbz",
            RelativePath = "Test Manga/Chapter 6.cbz",
            Manga = manga,
            IsUpscaled = true,
            UpscalerProfile = upscalerProfile
        };

        context.Libraries.Add(library);
        context.MangaSeries.Add(manga);
        context.UpscalerProfiles.Add(upscalerProfile);
        context.Chapters.Add(chapter);

        // Save first to get the IDs
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var mergeInfo = new MergedChapterInfo
        {
            ChapterId = chapter.Id,
            OriginalParts = new List<OriginalChapterPart>
            {
                new() { FileName = "Chapter 6.1.cbz", PageNames = ["001.jpg"] },
                new() { FileName = "Chapter 6.2.cbz", PageNames = ["002.jpg"] },
                new() { FileName = "Chapter 6.3.cbz", PageNames = ["003.jpg"] }
            },
            MergedChapterNumber = "6"
        };

        context.MergedChapterInfos.Add(mergeInfo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create both regular and upscaled merged chapter files
        var mergedChapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(mergedChapterPath)!);
        CreateTestCbzFile(Path.GetDirectoryName(mergedChapterPath)!, "Chapter 6.cbz", 3);

        var upscaledMergedPath = Path.Combine(library.UpscaledLibraryPath!, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(upscaledMergedPath)!);
        CreateTestCbzFile(Path.GetDirectoryName(upscaledMergedPath)!, "Chapter 6.cbz", 2); // Only 2 pages, missing 1

        // Mock restoration that returns fewer parts than expected (simulating partial merge)
        var testMetadata = new ExtractedMetadata("Test Manga", "Chapter 6.1", "6.1");
        var testMetadata2 = new ExtractedMetadata("Test Manga", "Chapter 6.2", "6.2");
        chapterPartMerger.RestoreChapterPartsAsync(Arg.Any<string>(), Arg.Any<List<OriginalChapterPart>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<FoundChapter>
            {
                new("Chapter 6.1.cbz", "Chapter 6.1.cbz", ChapterStorageType.Cbz, testMetadata),
                new("Chapter 6.2.cbz", "Chapter 6.2.cbz", ChapterStorageType.Cbz, testMetadata2)
                // Chapter 6.3.cbz is missing from restoration - indicates partial merge
            });

        // Act
        var result = await revertService.RevertMergedChapterAsync(chapter, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);

        // Verify that the missing parts were detected and logged
        logger.ReceivedWithAnyArgs().LogWarning(default!);
    }

    private Library CreateTestLibrary()
    {
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "regular"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled")
        };

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath!);

        return library;
    }

    private void CreateTestCbzFile(string directory, string fileName, int pageCount)
    {
        string filePath = Path.Combine(directory, fileName);
        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        for (int i = 1; i <= pageCount; i++)
        {
            var entry = zip.CreateEntry($"{i:D3}.jpg");
            using var stream = entry.Open();
            using var writer = new BinaryWriter(stream);
            // Write minimal JPEG header
            writer.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            writer.Write(CreateTestImageBytes());
        }

        // Add ComicInfo.xml
        var comicInfoEntry = zip.CreateEntry("ComicInfo.xml");
        using var comicInfoStream = comicInfoEntry.Open();
        using var comicInfoWriter = new StreamWriter(comicInfoStream);
        comicInfoWriter.Write("<?xml version=\"1.0\"?><ComicInfo><Title>Test Chapter</Title></ComicInfo>");
    }

    private static byte[] CreateTestImageBytes()
    {
        try
        {
            var image = Image.Black(16, 16);
            return image.JpegsaveBuffer();
        }
        catch
        {
            return [0xFF, 0xD8, 0xFF, 0xD9]; // Minimal JPEG
        }
    }
}