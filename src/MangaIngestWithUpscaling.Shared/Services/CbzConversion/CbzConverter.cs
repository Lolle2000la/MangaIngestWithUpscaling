using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Shared.Services.CbzConversion;

[RegisterScoped]
public class CbzConverter : ICbzConverter
{
    public FoundChapter ConvertToCbz(FoundChapter chapter, string foundIn)
    {
        if (chapter.StorageType == ChapterStorageType.Cbz)
            return chapter;

        if (chapter.StorageType == ChapterStorageType.Folder)
        {
            var targetPath = Path.Combine(foundIn, chapter.RelativePath);
            var cbzPath = Path.Combine(foundIn, $"{chapter.RelativePath}.cbz");
            var newRelativePath = Path.GetRelativePath(foundIn, cbzPath);

            // Create CBZ with corrected image file extensions
            CreateCbzWithCorrectedExtensions(targetPath, cbzPath);

            return chapter with
            {
                StorageType = ChapterStorageType.Cbz,
                RelativePath = newRelativePath,
                FileName = Path.GetFileName(cbzPath),
            };
        }

        throw new InvalidOperationException("Chapter is not in a supported format.");
    }

    /// <inheritdoc/>
    public bool FixImageExtensionsInCbz(string cbzPath)
    {
        if (!File.Exists(cbzPath))
            throw new FileNotFoundException($"CBZ file not found: {cbzPath}");

        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"cbz_fix_{Guid.NewGuid()}");
        var tempCbzPath = Path.Combine(Path.GetTempPath(), $"cbz_temp_{Guid.NewGuid()}.cbz");

        try
        {
            // Extract to temporary directory
            Directory.CreateDirectory(tempExtractDir);
            ZipFile.ExtractToDirectory(cbzPath, tempExtractDir);

            // Check if any extensions need fixing
            bool anyCorrections = false;
            foreach (
                var filePath in Directory.EnumerateFiles(
                    tempExtractDir,
                    "*",
                    SearchOption.AllDirectories
                )
            )
            {
                var currentExtension = Path.GetExtension(filePath);

                // Skip non-image files
                if (!ImageConstants.IsSupportedImageExtension(currentExtension))
                    continue;

                // Detect actual image format
                var detectedExtension = ImageConstants.DetectImageFormatFromFile(filePath);

                // If extension is incorrect, rename the file
                if (
                    detectedExtension != null
                    && !currentExtension.Equals(
                        detectedExtension,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    var directory = Path.GetDirectoryName(filePath)!;
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
                    var newFilePath = Path.Combine(
                        directory,
                        $"{fileNameWithoutExt}{detectedExtension}"
                    );

                    // Rename the file
                    File.Move(filePath, newFilePath);
                    anyCorrections = true;
                }
            }

            // If corrections were made, repackage the CBZ
            if (anyCorrections)
            {
                // Create new CBZ from corrected files
                ZipFile.CreateFromDirectory(tempExtractDir, tempCbzPath);

                // Replace original CBZ with corrected version
                File.Delete(cbzPath);
                File.Move(tempCbzPath, cbzPath);
            }

            return anyCorrections;
        }
        finally
        {
            // Clean up temporary files
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, true);

            if (File.Exists(tempCbzPath))
                File.Delete(tempCbzPath);
        }
    }

    private static void CreateCbzWithCorrectedExtensions(string sourceDirectory, string cbzPath)
    {
        using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Create);

        foreach (
            var filePath in Directory.EnumerateFiles(
                sourceDirectory,
                "*",
                SearchOption.AllDirectories
            )
        )
        {
            var currentExtension = Path.GetExtension(filePath);

            // Skip non-image files
            if (!ImageConstants.IsSupportedImageExtension(currentExtension))
            {
                // Add non-image files as-is
                var entryName = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
                archive.CreateEntryFromFile(filePath, entryName);
                continue;
            }

            // Detect actual image format from file header
            var detectedExtension = ImageConstants.DetectImageFormatFromFile(filePath);

            var entryPath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');

            // If extension is incorrect, correct it
            if (
                detectedExtension != null
                && !currentExtension.Equals(detectedExtension, StringComparison.OrdinalIgnoreCase)
            )
            {
                // Replace the extension in the entry name
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(entryPath);
                var directory = Path.GetDirectoryName(entryPath)?.Replace('\\', '/');
                entryPath = string.IsNullOrEmpty(directory)
                    ? $"{fileNameWithoutExt}{detectedExtension}"
                    : $"{directory}/{fileNameWithoutExt}{detectedExtension}";
            }

            archive.CreateEntryFromFile(filePath, entryPath);
        }
    }
}
