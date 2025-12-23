using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services;

public class ImageFormatConversionTests
{
    [Fact]
    public void ImageFormatConversionRule_ShouldInitializeWithDefaults()
    {
        var rule = new ImageFormatConversionRule { FromFormat = ".png", ToFormat = ".jpg" };

        Assert.Equal(".png", rule.FromFormat);
        Assert.Equal(".jpg", rule.ToFormat);
        Assert.Equal(95, rule.Quality);
    }

    [Fact]
    public void ImagePreprocessingOptions_ShouldSupportBothResizingAndConversion()
    {
        var options = new ImagePreprocessingOptions
        {
            MaxDimension = 2048,
            FormatConversionRules =
            [
                new ImageFormatConversionRule
                {
                    FromFormat = ".png",
                    ToFormat = ".jpg",
                    Quality = 90,
                },
            ],
        };

        Assert.Equal(2048, options.MaxDimension);
        Assert.Single(options.FormatConversionRules);
        Assert.Equal(".png", options.FormatConversionRules[0].FromFormat);
        Assert.Equal(".jpg", options.FormatConversionRules[0].ToFormat);
        Assert.Equal(90, options.FormatConversionRules[0].Quality);
    }

    [Fact]
    public void ImagePreprocessingOptions_ShouldAllowNullMaxDimension()
    {
        var options = new ImagePreprocessingOptions
        {
            MaxDimension = null,
            FormatConversionRules =
            [
                new ImageFormatConversionRule { FromFormat = ".webp", ToFormat = ".png" },
            ],
        };

        Assert.Null(options.MaxDimension);
        Assert.Single(options.FormatConversionRules);
    }

    [Fact]
    public void ImagePreprocessingOptions_ShouldAllowEmptyConversionRules()
    {
        var options = new ImagePreprocessingOptions
        {
            MaxDimension = 1024,
            FormatConversionRules = [],
        };

        Assert.Equal(1024, options.MaxDimension);
        Assert.Empty(options.FormatConversionRules);
    }
}
