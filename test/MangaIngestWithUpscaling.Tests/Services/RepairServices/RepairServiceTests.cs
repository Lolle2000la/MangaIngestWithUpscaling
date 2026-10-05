using System.IO.Compression;
using MangaIngestWithUpscaling.Services.RepairServices;
using Microsoft.Extensions.Logging;
using NSubstitute;

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

    [Fact]
    [Trait("Category", "Unit")]
    public void MergeRepairResults_ReplacesDestinationAtomically_AndLeavesNoTempSibling()
    {
        // Regression guard: the merge used to delete the destination before moving a temp from the
        // system temp directory, so a failed cross-volume copy lost the only upscaled archive.
        string workDir = Directory.CreateTempSubdirectory("repair-work-").FullName;
        string upscaledDir = Directory.CreateTempSubdirectory("repair-upscaled-").FullName;
        string destDir = Directory.CreateTempSubdirectory("repair-dest-").FullName;
        try
        {
            string destination = Path.Combine(destDir, "Chapter 1.cbz");
            File.WriteAllText(destination, "stale");

            File.WriteAllBytes(Path.Combine(upscaledDir, "001.png"), [1, 2, 3]);

            var context = new RepairContext
            {
                WorkDirectory = workDir,
                UpscaledDirectory = upscaledDir,
                MissingPagesCbz = string.Empty,
                UpscaledMissingCbz = string.Empty,
                HasMissingPages = false,
            };

            new RepairService().MergeRepairResults(context, destination, Substitute.For<ILogger>());

            Assert.True(File.Exists(destination));
            using var archive = ZipFile.OpenRead(destination);
            Assert.Contains(archive.Entries, e => e.Name == "001.png");
            Assert.Empty(Directory.EnumerateFiles(destDir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(upscaledDir, recursive: true);
            Directory.Delete(destDir, recursive: true);
        }
    }
}
