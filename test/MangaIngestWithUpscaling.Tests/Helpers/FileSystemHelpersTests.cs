using MangaIngestWithUpscaling.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Helpers;

public class FileSystemHelpersTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly ILogger _logger = Substitute.For<ILogger>();

    public FileSystemHelpersTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "fs_helpers_test_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }

    [Fact]
    public void TempSiblingPathFor_KeepsTheReplacementBesideItsDestination()
    {
        string destination = Path.Combine(_tempRoot, "library", "chapter1.cbz");

        string temp = FileSystemHelpers.TempSiblingPathFor(destination, "splits");

        // Beside the destination on purpose: moving the replacement onto the chapter is then a rename,
        // which a reader of the chapter cannot observe half-done.
        Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(temp));
        Assert.StartsWith($".{Path.GetFileName(destination)}.splits.", Path.GetFileName(temp));
        Assert.EndsWith(".tmp", temp, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteStaleTempSiblings_ReclaimsOnlyOldTempReplacements()
    {
        string directory = Path.Combine(_tempRoot, "library");
        Directory.CreateDirectory(directory);
        string stale = Path.Combine(directory, ".chapter1.cbz.splits.abcd.tmp");
        string inProgress = Path.Combine(directory, ".upscaled_7_abcd.tmp");
        string unrelatedTemp = Path.Combine(directory, "page.tmp");
        string chapter = Path.Combine(directory, "chapter1.cbz");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(inProgress, "in progress");
        File.WriteAllText(unrelatedTemp, "unrelated");
        File.WriteAllText(chapter, "chapter");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(unrelatedTemp, DateTime.UtcNow - TimeSpan.FromDays(2));

        FileSystemHelpers.DeleteStaleTempSiblings(directory, _logger);

        Assert.False(File.Exists(stale));
        // A replacement still being written — here or on another replica — keeps its temp.
        Assert.True(File.Exists(inProgress));
        // Only this codebase's dot-prefixed replacements are swept, never other temp-looking files.
        Assert.True(File.Exists(unrelatedTemp));
        Assert.True(File.Exists(chapter));
    }

    [Fact]
    public void DeleteStaleTempSiblings_WithMissingDirectory_DoesNotThrow()
    {
        Exception? exception = Record.Exception(() =>
            FileSystemHelpers.DeleteStaleTempSiblings(Path.Combine(_tempRoot, "missing"), _logger)
        );

        Assert.Null(exception);
    }

    [Fact]
    public void DeleteEmptySubfolders_WithMissingDirectory_DoesNotThrow()
    {
        string missing = Path.Combine(_tempRoot, "missing");

        Exception? exception = Record.Exception(() =>
            FileSystemHelpers.DeleteEmptySubfolders(missing, _logger)
        );

        Assert.Null(exception);
    }

    [Fact]
    public void DeleteEmptySubfolders_RemovesEmptyFoldersAndKeepsNonEmptyOnes()
    {
        string emptyNested = Path.Combine(_tempRoot, "a", "b");
        Directory.CreateDirectory(emptyNested);

        string keep = Path.Combine(_tempRoot, "keep");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "file.txt"), "content");

        FileSystemHelpers.DeleteEmptySubfolders(_tempRoot, _logger);

        Assert.False(Directory.Exists(emptyNested));
        Assert.True(Directory.Exists(keep));
    }
}
