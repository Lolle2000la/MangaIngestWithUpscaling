using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using System.ComponentModel.DataAnnotations;

namespace MangaIngestWithUpscaling.Shared.Tests.Data.LibraryManagement;

public class UpscalerProfileTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    [Trait("Category", "Unit")]
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
    [Trait("Category", "Unit")]
    public void UpscalerProfile_WithValidData_ShouldPassValidation()
    {
        // Arrange
        var profile = new UpscalerProfile
        {
            Name = "Valid Profile Name",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 80
        };

        var context = new ValidationContext(profile);
        var results = new List<ValidationResult>();

        // Act
        var isValid = Validator.TryValidateObject(profile, context, results, true);

        // Assert
        Assert.True(isValid);
        Assert.Empty(results);
    }
}