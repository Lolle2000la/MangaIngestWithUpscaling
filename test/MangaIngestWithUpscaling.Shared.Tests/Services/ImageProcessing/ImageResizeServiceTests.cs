using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.ImageProcessing;

public class ImageResizeServiceTests
{
    private readonly IFileSystem _mockFileSystem;
    private readonly ILogger<ImageResizeService> _mockLogger;
    private readonly IStringLocalizer<ImageResizeService> _mockLocalizer;
    private readonly ImageResizeService _service;

    public ImageResizeServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<ImageResizeService>>();
        _mockFileSystem = Substitute.For<IFileSystem>();
        _mockLocalizer = Substitute.For<IStringLocalizer<ImageResizeService>>();

        _mockLocalizer["Error_MaxDimensionMustBePositive"]
            .Returns(new LocalizedString("Error_MaxDimensionMustBePositive", "Maximum dimension must be greater than 0"));
        
        _mockLocalizer["Error_InputCbzFileNotFound", Arg.Any<object[]>()]
            .Returns(x => new LocalizedString("Error_InputCbzFileNotFound", $"File not found: {x.Arg<object[]>()[0]}"));

        _service = new ImageResizeService(
            _mockLogger,
            _mockFileSystem,
            _mockLocalizer
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    [Trait("Category", "Unit")]
    public async Task CreateResizedTempCbzAsync_InvalidMaxDimension_ShouldThrowArgumentException(
        int maxDimension
    )
    {
        // Arrange
        var tempInputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cbz");

        // Create a temporary file to pass the file existence check
        await File.WriteAllTextAsync(
            tempInputPath,
            "dummy content",
            TestContext.Current.CancellationToken
        );

        try
        {
            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateResizedTempCbzAsync(
                    tempInputPath,
                    maxDimension,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.Equal("options", exception.ParamName);
            Assert.Contains("Maximum dimension must be greater than 0", exception.Message);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateResizedTempCbzAsync_InputFileNotFound_ShouldThrowFileNotFoundException()
    {
        // Arrange
        const string nonExistentPath = "nonexistent_file_that_does_not_exist.cbz";
        const int maxDimension = 1024;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.CreateResizedTempCbzAsync(
                nonExistentPath,
                maxDimension,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains(nonExistentPath, exception.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFile_ExistingFile_ShouldDeleteFile()
    {
        // Arrange
        string tempFilePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.tmp");
        File.WriteAllText(tempFilePath, "test content");

        // Ensure file exists before test
        Assert.True(File.Exists(tempFilePath));

        // Act
        _service.CleanupTempFile(tempFilePath);

        // Assert
        Assert.False(File.Exists(tempFilePath));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFile_NonExistentFile_ShouldNotThrow()
    {
        // Arrange
        const string nonExistentPath = "definitely_nonexistent_file.tmp";

        // Act & Assert - Should not throw
        var exception = Record.Exception(() => _service.CleanupTempFile(nonExistentPath));
        Assert.Null(exception);
    }
}
