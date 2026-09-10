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
