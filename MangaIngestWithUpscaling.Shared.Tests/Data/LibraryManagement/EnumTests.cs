using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Tests.Data.LibraryManagement;

public class EnumTests
{
    [Theory]
    [InlineData(ScaleFactor.OneX, 1)]
    [InlineData(ScaleFactor.TwoX, 2)]
    [InlineData(ScaleFactor.ThreeX, 3)]
    [InlineData(ScaleFactor.FourX, 4)]
    public void ScaleFactor_EnumValues_ShouldHaveCorrectIntegerValues(ScaleFactor scaleFactor, int expectedValue)
    {
        // Act & Assert
        Assert.Equal(expectedValue, (int)scaleFactor);
    }

    [Theory]
    [InlineData(ScaleFactor.OneX, "1x")]
    [InlineData(ScaleFactor.TwoX, "2x")]
    [InlineData(ScaleFactor.ThreeX, "3x")]
    [InlineData(ScaleFactor.FourX, "4x")]
    public void ScaleFactor_DisplayNames_ShouldBeCorrect(ScaleFactor scaleFactor, string expectedDisplayName)
    {
        // Arrange
        var field = scaleFactor.GetType().GetField(scaleFactor.ToString());
        var displayAttribute = field?.GetCustomAttribute<DisplayAttribute>();

        // Act & Assert
        Assert.NotNull(displayAttribute);
        Assert.Equal(expectedDisplayName, displayAttribute.Name);
    }

    [Theory]
    [InlineData(CompressionFormat.Avif, "AVIF")]
    [InlineData(CompressionFormat.Png, "PNG")]
    [InlineData(CompressionFormat.Webp, "WebP")]
    [InlineData(CompressionFormat.Jpg, "JPEG")]
    public void CompressionFormat_DisplayNames_ShouldBeCorrect(CompressionFormat format, string expectedDisplayName)
    {
        // Arrange
        var field = format.GetType().GetField(format.ToString());
        var displayAttribute = field?.GetCustomAttribute<DisplayAttribute>();

        // Act & Assert
        Assert.NotNull(displayAttribute);
        Assert.Equal(expectedDisplayName, displayAttribute.Name);
    }

    [Fact]
    public void UpscalerMethod_ShouldOnlyHaveMangaJaNai()
    {
        // Arrange
        var values = Enum.GetValues<UpscalerMethod>();

        // Act & Assert
        Assert.Single(values);
        Assert.Equal(UpscalerMethod.MangaJaNai, values[0]);
    }
}