using System.ComponentModel.DataAnnotations;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Tests.Data.LibraryManagement;

public class UpscalerProfileTests
{
    [Fact]
    public void UpscalerProfile_InitialState_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        // Assert
        Assert.Equal(0, profile.Id);
        Assert.Equal("Test Profile", profile.Name);
        Assert.Equal(UpscalerMethod.MangaJaNai, profile.UpscalerMethod);
        Assert.Equal(ScaleFactor.TwoX, profile.ScalingFactor);
        Assert.Equal(CompressionFormat.Png, profile.CompressionFormat);
        Assert.Equal(80, profile.Quality);
        Assert.False(profile.Deleted);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    public void UpscalerProfile_Quality_ShouldValidateRange(int quality, bool isValid)
    {
        // Arrange
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = quality
        };

        var context = new ValidationContext(profile);
        var results = new List<ValidationResult>();

        // Act
        var actual = Validator.TryValidateObject(profile, context, results, true);

        // Assert
        Assert.Equal(isValid, actual);
        if (!isValid)
        {
            Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpscalerProfile.Quality)));
        }
    }

    [Fact]
    public void UpscalerProfile_SetDeleted_ShouldUpdateDeletedFlag()
    {
        // Arrange
        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        // Act
        profile.Deleted = true;

        // Assert
        Assert.True(profile.Deleted);
    }
}