using MangaIngestWithUpscaling.Services.RepairServices;

namespace MangaIngestWithUpscaling.Tests.Services.RepairServices;

public class RepairServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void RepairContext_Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var context = new RepairContext();

        // Assert
        Assert.Equal(string.Empty, context.WorkDirectory);
        Assert.Equal(string.Empty, context.UpscaledDirectory);
        Assert.Equal(string.Empty, context.MissingPagesCbz);
        Assert.Equal(string.Empty, context.UpscaledMissingCbz);
        Assert.False(context.HasMissingPages);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RepairContext_Properties_ShouldBeSettable()
    {
        // Arrange
        var context = new RepairContext();
        const string workDir = "/tmp/work";
        const string upscaledDir = "/tmp/upscaled";
        const string missingPagesCbz = "/tmp/missing.cbz";
        const string upscaledMissingCbz = "/tmp/upscaled_missing.cbz";

        // Act
        context.WorkDirectory = workDir;
        context.UpscaledDirectory = upscaledDir;
        context.MissingPagesCbz = missingPagesCbz;
        context.UpscaledMissingCbz = upscaledMissingCbz;
        context.HasMissingPages = true;

        // Assert
        Assert.Equal(workDir, context.WorkDirectory);
        Assert.Equal(upscaledDir, context.UpscaledDirectory);
        Assert.Equal(missingPagesCbz, context.MissingPagesCbz);
        Assert.Equal(upscaledMissingCbz, context.UpscaledMissingCbz);
        Assert.True(context.HasMissingPages);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RepairContext_Dispose_ShouldNotThrow()
    {
        // Arrange
        var context = new RepairContext();

        // Act & Assert
        var exception = Record.Exception(() => context.Dispose());
        Assert.Null(exception);
    }
}