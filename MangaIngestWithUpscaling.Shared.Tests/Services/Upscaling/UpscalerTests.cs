using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Moq;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class UpscalerTests
{
    [Fact]
    public void UpscaleProgress_Constructor_ShouldSetProperties()
    {
        // Arrange
        const int total = 100;
        const int current = 50;
        const string phase = "Processing";
        const string statusMessage = "Status update";

        // Act
        var progress = new UpscaleProgress(total, current, phase, statusMessage);

        // Assert
        Assert.Equal(total, progress.Total);
        Assert.Equal(current, progress.Current);
        Assert.Equal(phase, progress.Phase);
        Assert.Equal(statusMessage, progress.StatusMessage);
    }

    [Fact]
    public void UpscaleProgress_ConstructorWithNulls_ShouldAcceptNullValues()
    {
        // Act
        var progress = new UpscaleProgress(null, null, null, null);

        // Assert
        Assert.Null(progress.Total);
        Assert.Null(progress.Current);
        Assert.Null(progress.Phase);
        Assert.Null(progress.StatusMessage);
    }

    [Fact]
    public async Task IUpscaler_UpscaleWithoutProgress_ShouldCallImplementation()
    {
        // Arrange
        var mockUpscaler = new Mock<IUpscaler>();
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Act
        await mockUpscaler.Object.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        mockUpscaler.Verify(u => u.Upscale(inputPath, outputPath, profile, cancellationToken), Times.Once);
    }

    [Fact]
    public async Task IUpscaler_UpscaleWithProgress_ShouldCallImplementation()
    {
        // Arrange
        var mockUpscaler = new Mock<IUpscaler>();
        var mockProgress = new Mock<IProgress<UpscaleProgress>>();
        const string inputPath = "input.cbz";
        const string outputPath = "output.cbz";
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };
        var cancellationToken = CancellationToken.None;

        // Act
        await mockUpscaler.Object.Upscale(inputPath, outputPath, profile, mockProgress.Object, cancellationToken);

        // Assert
        mockUpscaler.Verify(u => u.Upscale(inputPath, outputPath, profile, mockProgress.Object, cancellationToken), Times.Once);
    }

    [Fact]
    public async Task IUpscaler_DownloadModelsIfNecessary_ShouldCallImplementation()
    {
        // Arrange
        var mockUpscaler = new Mock<IUpscaler>();
        var cancellationToken = CancellationToken.None;

        // Act
        await mockUpscaler.Object.DownloadModelsIfNecessary(cancellationToken);

        // Assert
        mockUpscaler.Verify(u => u.DownloadModelsIfNecessary(cancellationToken), Times.Once);
    }
}