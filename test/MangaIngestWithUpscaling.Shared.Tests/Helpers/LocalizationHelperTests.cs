using System.Globalization;
using MangaIngestWithUpscaling.Shared.Helpers;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Helpers;

public class LocalizationHelperTests
{
    [Theory]
    [InlineData("Chapter", 1, "en-US", "1 Chapter")]
    [InlineData("Chapter", 3, "en-US", "3 Chapters")]
    [InlineData("チャプター", 1, "ja-JP", "1 チャプター")]
    [InlineData("チャプター", 3, "ja-JP", "3 チャプター")]
    [InlineData("Manga", 0, "en-US", "0 Mangas")]
    [InlineData("マンガ", 0, "ja-JP", "0 マンガ")]
    public void ToLocalizedQuantity_ReturnsCorrectlyFormattedString(
        string word,
        int quantity,
        string cultureName,
        string expected
    )
    {
        // Arrange
        var culture = new CultureInfo(cultureName);

        // Act
        var result = word.ToLocalizedQuantity(quantity, culture);

        // Assert
        Assert.Equal(expected, result);
    }
}
