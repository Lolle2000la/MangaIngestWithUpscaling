using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using NSubstitute;

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
        var mockUpscaler = Substitute.For<IUpscaler>();
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
        await mockUpscaler.Upscale(inputPath, outputPath, profile, cancellationToken);

        // Assert
        await mockUpscaler.Received(1).Upscale(inputPath, outputPath, profile, cancellationToken);
    }

    [Fact]
    public async Task IUpscaler_UpscaleWithProgress_ShouldCallImplementation()
    {
        // Arrange
        var mockUpscaler = Substitute.For<IUpscaler>();
        var mockProgress = Substitute.For<IProgress<UpscaleProgress>>();
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
        await mockUpscaler.Upscale(inputPath, outputPath, profile, mockProgress, cancellationToken);

        // Assert
        await mockUpscaler.Received(1).Upscale(inputPath, outputPath, profile, mockProgress, cancellationToken);
    }

    [Fact]
    public async Task IUpscaler_DownloadModelsIfNecessary_ShouldCallImplementation()
    {
        // Arrange
        var mockUpscaler = Substitute.For<IUpscaler>();
        var cancellationToken = CancellationToken.None;

        // Act
        await mockUpscaler.DownloadModelsIfNecessary(cancellationToken);

        // Assert
        await mockUpscaler.Received(1).DownloadModelsIfNecessary(cancellationToken);
    }
}