using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Services.CbzConversion;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.CbzConversion;

public class CbzConverterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CbzConverter _converter;

    public CbzConverterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _converter = new CbzConverter(Substitute.For<IStringLocalizer<CbzConverter>>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void ConvertToCbz_ReturnsSameChapter_WhenAlreadyCbz()
    {
        var chapter = new FoundChapter(
            "test.cbz",
            "test.cbz",
            ChapterStorageType.Cbz,
            new ExtractedMetadata("Test", "1", null)
        );

        var result = _converter.ConvertToCbz(chapter, _tempDir);

        Assert.Same(chapter, result);
    }

    [Fact]
    public void ConvertToCbz_CorrectsMismatchedImageExtensions()
    {
        var chapterDir = Path.Combine(_tempDir, "TestChapter");
        Directory.CreateDirectory(chapterDir);

        // Create a WebP file with .jpg extension (mismatched)
        byte[] webpHeader =
        [
            0x52,
            0x49,
            0x46,
            0x46,
            0x00,
            0x00,
            0x00,
            0x00,
            0x57,
            0x45,
            0x42,
            0x50,
        ];
        var mismatchedFile = Path.Combine(chapterDir, "page001.jpg");
        File.WriteAllBytes(mismatchedFile, webpHeader);

        // Create a proper JPEG file with .jpg extension (correct)
        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        var correctFile = Path.Combine(chapterDir, "page002.jpg");
        File.WriteAllBytes(correctFile, jpegHeader);

        var chapter = new FoundChapter(
            "TestChapter",
            "TestChapter",
            ChapterStorageType.Folder,
            new ExtractedMetadata("Test", "1", null)
        );

        var result = _converter.ConvertToCbz(chapter, _tempDir);

        Assert.Equal(ChapterStorageType.Cbz, result.StorageType);
        Assert.Equal("TestChapter.cbz", result.FileName);

        var cbzPath = Path.Combine(_tempDir, "TestChapter.cbz");
        Assert.True(File.Exists(cbzPath));

        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains("page001.webp", entries);
        Assert.Contains("page002.jpg", entries);
    }

    [Fact]
    public void ConvertToCbz_PreservesNonImageFiles()
    {
        var chapterDir = Path.Combine(_tempDir, "TestChapter");
        Directory.CreateDirectory(chapterDir);

        // Create an image file
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(Path.Combine(chapterDir, "page001.png"), pngHeader);

        // Create a non-image file (ComicInfo.xml)
        File.WriteAllText(Path.Combine(chapterDir, "ComicInfo.xml"), "<ComicInfo></ComicInfo>");

        var chapter = new FoundChapter(
            "TestChapter",
            "TestChapter",
            ChapterStorageType.Folder,
            new ExtractedMetadata("Test", "1", null)
        );

        var result = _converter.ConvertToCbz(chapter, _tempDir);

        var cbzPath = Path.Combine(_tempDir, "TestChapter.cbz");
        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains("ComicInfo.xml", entries);
        Assert.Contains("page001.png", entries);
    }

    [Fact]
    public void ConvertToCbz_PreservesDirectoryStructure()
    {
        var chapterDir = Path.Combine(_tempDir, "TestChapter");
        var subDir = Path.Combine(chapterDir, "subfolder");
        Directory.CreateDirectory(subDir);

        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        File.WriteAllBytes(Path.Combine(chapterDir, "page001.jpg"), jpegHeader);
        File.WriteAllBytes(Path.Combine(subDir, "page002.jpg"), jpegHeader);

        var chapter = new FoundChapter(
            "TestChapter",
            "TestChapter",
            ChapterStorageType.Folder,
            new ExtractedMetadata("Test", "1", null)
        );

        var result = _converter.ConvertToCbz(chapter, _tempDir);

        var cbzPath = Path.Combine(_tempDir, "TestChapter.cbz");
        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains("page001.jpg", entries);
        Assert.Contains("subfolder/page002.jpg", entries);
    }

    [Fact]
    public void ConvertToCbz_CorrectsMismatchedExtensionInSubdirectory()
    {
        var chapterDir = Path.Combine(_tempDir, "TestChapter");
        var subDir = Path.Combine(chapterDir, "images");
        Directory.CreateDirectory(subDir);

        // Create a PNG file with .jpg extension in subdirectory
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(Path.Combine(subDir, "page001.jpg"), pngHeader);

        var chapter = new FoundChapter(
            "TestChapter",
            "TestChapter",
            ChapterStorageType.Folder,
            new ExtractedMetadata("Test", "1", null)
        );

        var result = _converter.ConvertToCbz(chapter, _tempDir);

        var cbzPath = Path.Combine(_tempDir, "TestChapter.cbz");
        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Single(entries);
        Assert.Equal("images/page001.png", entries[0]);
    }

    [Fact]
    public void ConvertToCbz_ThrowsForUnsupportedStorageType()
    {
        var chapter = new FoundChapter(
            "test",
            "test",
            (ChapterStorageType)999,
            new ExtractedMetadata("Test", "1", null)
        );

        Assert.Throws<InvalidOperationException>(() => _converter.ConvertToCbz(chapter, _tempDir));
    }

    [Fact]
    public void FixImageExtensionsInCbz_CorrectsMismatchedExtensions()
    {
        // Create a CBZ with mismatched extensions
        var tempCbzDir = Path.Combine(_tempDir, "TestChapter");
        Directory.CreateDirectory(tempCbzDir);

        // Create a WebP file with .jpg extension
        byte[] webpHeader =
        [
            0x52,
            0x49,
            0x46,
            0x46,
            0x00,
            0x00,
            0x00,
            0x00,
            0x57,
            0x45,
            0x42,
            0x50,
        ];
        File.WriteAllBytes(Path.Combine(tempCbzDir, "page001.jpg"), webpHeader);

        // Create a PNG file with .jpg extension
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(Path.Combine(tempCbzDir, "page002.jpg"), pngHeader);

        // Create CBZ
        var cbzPath = Path.Combine(_tempDir, "test.cbz");
        ZipFile.CreateFromDirectory(tempCbzDir, cbzPath);

        // Fix extensions
        bool result = _converter.FixImageExtensionsInCbz(cbzPath);

        // Verify corrections were made
        Assert.True(result);

        // Verify CBZ contents
        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).OrderBy(e => e).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains("page001.webp", entries);
        Assert.Contains("page002.png", entries);
    }

    [Fact]
    public void FixImageExtensionsInCbz_ReturnsFalseWhenNoChangesNeeded()
    {
        // Create a CBZ with correct extensions
        var tempCbzDir = Path.Combine(_tempDir, "TestChapter");
        Directory.CreateDirectory(tempCbzDir);

        // Create a proper JPEG file with .jpg extension
        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        File.WriteAllBytes(Path.Combine(tempCbzDir, "page001.jpg"), jpegHeader);

        // Create CBZ
        var cbzPath = Path.Combine(_tempDir, "test.cbz");
        ZipFile.CreateFromDirectory(tempCbzDir, cbzPath);

        // Try to fix extensions
        bool result = _converter.FixImageExtensionsInCbz(cbzPath);

        // No corrections should be made
        Assert.False(result);

        // Verify CBZ contents unchanged
        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Single(entries);
        Assert.Equal("page001.jpg", entries[0]);
    }

    [Fact]
    public void FixImageExtensionsInCbz_ThrowsForNonExistentFile()
    {
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.cbz");
        Assert.Throws<FileNotFoundException>(() =>
            _converter.FixImageExtensionsInCbz(nonExistentPath)
        );
    }

    [Fact]
    public void FixImageExtensionsInCbz_PreservesNonImageFiles()
    {
        // Create a CBZ with image and non-image files
        var tempCbzDir = Path.Combine(_tempDir, "TestChapter");
        Directory.CreateDirectory(tempCbzDir);

        // Create a WebP file with .jpg extension
        byte[] webpHeader =
        [
            0x52,
            0x49,
            0x46,
            0x46,
            0x00,
            0x00,
            0x00,
            0x00,
            0x57,
            0x45,
            0x42,
            0x50,
        ];
        File.WriteAllBytes(Path.Combine(tempCbzDir, "page001.jpg"), webpHeader);

        // Create a non-image file
        File.WriteAllText(Path.Combine(tempCbzDir, "ComicInfo.xml"), "<ComicInfo></ComicInfo>");

        // Create CBZ
        var cbzPath = Path.Combine(_tempDir, "test.cbz");
        ZipFile.CreateFromDirectory(tempCbzDir, cbzPath);

        // Fix extensions
        bool result = _converter.FixImageExtensionsInCbz(cbzPath);

        // Verify corrections were made
        Assert.True(result);

        // Verify CBZ contents
        using var archive = ZipFile.OpenRead(cbzPath);
        var entries = archive.Entries.Select(e => e.FullName).OrderBy(e => e).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains("ComicInfo.xml", entries);
        Assert.Contains("page001.webp", entries);
    }
}
