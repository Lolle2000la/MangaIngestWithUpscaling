using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.Extensions.Logging.Abstractions;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.MetadataHandling;

public class MetadataHandlingServiceTests
{
    private readonly MetadataHandlingService _service;

    public MetadataHandlingServiceTests()
    {
        _service = new MetadataHandlingService(NullLogger<MetadataHandlingService>.Instance);
    }

    [Fact]
    public async Task GetSeriesAndTitleFromComicInfoAsync_ReturnsEmpty_WhenFileDoesNotExist()
    {
        // Act
        var result = await _service.GetSeriesAndTitleFromComicInfoAsync("nonexistent.cbz");

        // Assert
        Assert.Equal(string.Empty, result.Series);
        Assert.Equal(string.Empty, result.ChapterTitle);
        Assert.Equal(string.Empty, result.Number);
    }

    [Fact]
    public async Task PagesEqualAsync_ReturnsTrue_WhenNeitherFileExists()
    {
        // Act
        var result = await _service.PagesEqualAsync("nonexistent1.cbz", "nonexistent2.cbz");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task AnalyzePageDifferencesAsync_ReturnsEmptyResult_WhenFilesDoNotExist()
    {
        // Act
        var result = await _service.AnalyzePageDifferencesAsync(
            "nonexistent1.cbz",
            "nonexistent2.cbz"
        );

        // Assert
        Assert.Empty(result.MissingPages);
        Assert.Empty(result.ExtraPages);
        Assert.True(result.AreEqual);
        Assert.False(result.CanRepair);
    }

    [Fact]
    public async Task AnalyzePageDifferencesAsync_FlagsACorruptArchive()
    {
        string corrupt = Path.Combine(Path.GetTempPath(), $"corrupt_{Guid.NewGuid():N}.cbz");
        await File.WriteAllTextAsync(corrupt, "not a zip", TestContext.Current.CancellationToken);
        try
        {
            var result = await _service.AnalyzePageDifferencesAsync(corrupt, "nonexistent.cbz");

            // Reported as "no differences", but flagged so the repair path can classify it as terminal
            // instead of a transient restart.
            Assert.Empty(result.MissingPages);
            Assert.True(result.Corrupt);
        }
        finally
        {
            File.Delete(corrupt);
        }
    }

    [Fact]
    public async Task WriteComicInfoAsync_DoesNotThrow_WhenFileDoesNotExist()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(() =>
            _service.WriteComicInfoAsync("nonexistent.cbz", new ExtractedMetadata("S", "T", "1"))
        );

        Assert.Null(exception);
    }
}
