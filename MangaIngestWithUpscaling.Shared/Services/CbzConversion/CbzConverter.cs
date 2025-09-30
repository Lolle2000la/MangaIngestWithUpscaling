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
