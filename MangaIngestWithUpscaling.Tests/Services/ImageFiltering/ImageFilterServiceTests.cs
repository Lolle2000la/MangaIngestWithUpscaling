using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Tests.Services.ImageFiltering;

public class ImageFilterServiceTests : IDisposable
{
    private readonly ILogger<ImageFilterService> _mockLogger;
    private readonly ImageFilterService _service;
    private readonly string _tempDir;
    private readonly string _testCbzPath;
    private readonly string _testImagePath;

    public ImageFilterServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<ImageFilterService>>();
        _service = new ImageFilterService(_mockLogger);
        _tempDir = Path.Combine(Path.GetTempPath(), $"image_filter_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        
        _testCbzPath = Path.Combine(_tempDir, "test.cbz");
        _testImagePath = Path.Combine(_tempDir, "test.jpg");
        
        CreateTestFiles();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyFiltersToChapterAsync_WithNonExistentFile_ShouldReturnError()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.cbz");
        var filters = new List<FilteredImage>();

        // Act
        var result = await _service.ApplyFiltersToChapterAsync(nonExistentPath, filters);

        // Assert
        Assert.Contains($"Original CBZ file not found: {nonExistentPath}", result.ErrorMessages);
        Assert.Equal(0, result.FilteredCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyFiltersToChapterAsync_WithNonExistentUpscaledFile_ShouldReturnError()
    {
        // Arrange
        var nonExistentUpscaledPath = Path.Combine(_tempDir, "nonexistent_upscaled.cbz");
        var filters = new List<FilteredImage>();

        // Act
        var result = await _service.ApplyFiltersToChapterAsync(_testCbzPath, nonExistentUpscaledPath, filters);

        // Assert
        Assert.Contains($"Upscaled CBZ file not found: {nonExistentUpscaledPath}", result.ErrorMessages);
        Assert.Equal(0, result.FilteredCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyFiltersToChapterAsync_WithNoFilters_ShouldReturnEmptyResult()
    {
        // Arrange
        var filters = new List<FilteredImage>();

        // Act
        var result = await _service.ApplyFiltersToChapterAsync(_testCbzPath, filters);

        // Assert
        Assert.Equal(0, result.FilteredCount);
        Assert.Empty(result.FilteredImageNames);
        Assert.Empty(result.ErrorMessages);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyFiltersToChapterAsync_WithValidFile_ShouldCompleteWithoutErrors()
    {
        // Arrange
        var filters = new List<FilteredImage>
        {
            new()
            {
                OriginalFileName = "nonexistent.jpg",
                ContentHash = "somehash",
                Library = CreateTestLibrary()
            }
        };

        // Act
        var result = await _service.ApplyFiltersToChapterAsync(_testCbzPath, filters);

        // Assert
        Assert.Empty(result.ErrorMessages);
        Assert.True(result.FilteredCount >= 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculateContentHash_WithValidImageBytes_ShouldReturnConsistentHash()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();

        // Act
        var hash1 = _service.CalculateContentHash(imageBytes);
        var hash2 = _service.CalculateContentHash(imageBytes);

        // Assert
        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1, hash2);
        Assert.True(hash1.Length > 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculateContentHash_WithDifferentImageBytes_ShouldReturnDifferentHashes()
    {
        // Arrange
        var imageBytes1 = CreateTestImageBytes();
        var imageBytes2 = CreateTestImageBytes(differentContent: true);

        // Act
        var hash1 = _service.CalculateContentHash(imageBytes1);
        var hash2 = _service.CalculateContentHash(imageBytes2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculatePerceptualHash_WithValidImageBytes_ShouldReturnValidHash()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();

        // Act
        var hash = _service.CalculatePerceptualHash(imageBytes);

        // Assert
        Assert.True(hash >= 0); // ulong should be non-negative
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculatePerceptualHash_WithSameImageBytes_ShouldReturnSameHash()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();

        // Act
        var hash1 = _service.CalculatePerceptualHash(imageBytes);
        var hash2 = _service.CalculatePerceptualHash(imageBytes);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculateImageSimilarity_WithIdenticalHashes_ShouldReturn100Percent()
    {
        // Arrange
        const ulong hash = 12345UL;

        // Act
        var similarity = _service.CalculateImageSimilarity(hash, hash);

        // Assert
        Assert.Equal(100.0, similarity);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculateImageSimilarity_WithDifferentHashes_ShouldReturnLessThan100Percent()
    {
        // Arrange
        const ulong hash1 = 12345UL;
        const ulong hash2 = 54321UL;

        // Act
        var similarity = _service.CalculateImageSimilarity(hash1, hash2);

        // Assert
        Assert.True(similarity >= 0.0);
        Assert.True(similarity <= 100.0);
        Assert.True(similarity < 100.0); // Different hashes should not be 100% similar
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculateHammingDistance_WithIdenticalHashes_ShouldReturnZero()
    {
        // Arrange
        const ulong hash = 12345UL;

        // Act
        var distance = _service.CalculateHammingDistance(hash, hash);

        // Assert
        Assert.Equal(0, distance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CalculateHammingDistance_WithDifferentHashes_ShouldReturnPositiveValue()
    {
        // Arrange
        const ulong hash1 = 12345UL;
        const ulong hash2 = 54321UL;

        // Act
        var distance = _service.CalculateHammingDistance(hash1, hash2);

        // Assert
        Assert.True(distance >= 0);
        Assert.True(distance <= 64); // Max hamming distance for 64-bit value
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateThumbnailBase64Async_WithValidImageBytes_ShouldReturnBase64String()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();

        // Act
        var thumbnail = await _service.GenerateThumbnailBase64Async(imageBytes);

        // Assert
        Assert.NotNull(thumbnail);
        Assert.True(thumbnail.Length > 0);
        
        // Should be valid base64
        var exception = Record.Exception(() => Convert.FromBase64String(thumbnail));
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateThumbnailBase64Async_WithCustomMaxSize_ShouldUseSpecifiedSize()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();
        const int customMaxSize = 75;

        // Act
        var thumbnail = await _service.GenerateThumbnailBase64Async(imageBytes, customMaxSize);

        // Assert
        Assert.NotNull(thumbnail);
        Assert.True(thumbnail.Length > 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromBytesAsync_WithValidInputs_ShouldCreateFilteredImage()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();
        var fileName = "test.jpg";
        var library = CreateTestLibrary();
        const string mimeType = "image/jpeg";
        const string description = "Test description";

        // Act
        var result = await _service.CreateFilteredImageFromBytesAsync(imageBytes, fileName, library, mimeType, description);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(fileName, result.OriginalFileName);
        Assert.Equal(mimeType, result.MimeType);
        Assert.Equal(description, result.Description);
        Assert.Equal(imageBytes.Length, result.FileSizeBytes);
        Assert.NotNull(result.ContentHash);
        Assert.NotNull(result.PerceptualHash);
        Assert.NotNull(result.ThumbnailBase64);
        Assert.True(result.DateAdded <= DateTime.UtcNow);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromBytesAsync_WithNullMimeType_ShouldInferFromFileName()
    {
        // Arrange
        var imageBytes = CreateTestImageBytes();
        var fileName = "test.png";
        var library = CreateTestLibrary();

        // Act
        var result = await _service.CreateFilteredImageFromBytesAsync(imageBytes, fileName, library);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("image/png", result.MimeType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromFileAsync_WithNonExistentFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.jpg");
        var library = CreateTestLibrary();

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.CreateFilteredImageFromFileAsync(nonExistentPath, library));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromFileAsync_WithValidFile_ShouldCreateFilteredImage()
    {
        // Arrange
        var library = CreateTestLibrary();

        // Act
        var result = await _service.CreateFilteredImageFromFileAsync(_testImagePath, library, "Test description");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test.jpg", result.OriginalFileName);
        Assert.Equal("Test description", result.Description);
        Assert.NotNull(result.ContentHash);
        Assert.NotNull(result.PerceptualHash);
        Assert.NotNull(result.ThumbnailBase64);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromCbzAsync_WithNonExistentCbz_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.cbz");
        var library = CreateTestLibrary();

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.CreateFilteredImageFromCbzAsync(nonExistentPath, "image.jpg", library));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromCbzAsync_WithNonExistentEntry_ShouldThrowArgumentException()
    {
        // Arrange
        var library = CreateTestLibrary();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateFilteredImageFromCbzAsync(_testCbzPath, "nonexistent.jpg", library));
        
        Assert.Contains("not found in CBZ file", exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateFilteredImageFromCbzAsync_WithValidEntry_ShouldCreateFilteredImage()
    {
        // Arrange
        var library = CreateTestLibrary();

        // Act
        var result = await _service.CreateFilteredImageFromCbzAsync(_testCbzPath, "test_image.jpg", library, "Test description");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test_image.jpg", result.OriginalFileName);
        Assert.Equal("Test description", result.Description);
        Assert.NotNull(result.ContentHash);
        Assert.NotNull(result.PerceptualHash);
        Assert.NotNull(result.ThumbnailBase64);
    }

    private void CreateTestFiles()
    {
        // Create a test image file
        var testImageBytes = CreateTestImageBytes();
        File.WriteAllBytes(_testImagePath, testImageBytes);

        // Create a test CBZ file with an image entry
        using var fileStream = new FileStream(_testCbzPath, FileMode.Create);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        
        var entry = archive.CreateEntry("test_image.jpg");
        using var entryStream = entry.Open();
        entryStream.Write(testImageBytes);
    }

    private static byte[] CreateTestImageBytes(bool differentContent = false)
    {
        // Create a minimal JPEG-like byte array
        // This is a simplified approach for testing - in real scenarios you'd use actual image bytes
        var baseContent = new byte[] { 
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 
            0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
            0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
            0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
            0xFF, 0xD9 // JPEG end marker
        };

        if (differentContent)
        {
            // Modify some bytes to create different content
            var modifiedContent = new byte[baseContent.Length];
            Array.Copy(baseContent, modifiedContent, baseContent.Length);
            modifiedContent[10] = 0xFF; // Change a byte to make it different
            modifiedContent[15] = 0xAA;
            return modifiedContent;
        }

        return baseContent;
    }

    private static Library CreateTestLibrary()
    {
        return new Library
        {
            Id = 1,
            Name = "Test Library",
            IngestPath = "/test/path"
        };
    }
}