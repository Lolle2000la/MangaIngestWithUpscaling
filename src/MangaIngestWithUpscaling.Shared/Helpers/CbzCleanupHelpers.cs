using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Constants;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Helpers;

public static class CbzCleanupHelpers
{
    /// <summary>
    /// Removes a specific image by name from a CBZ archive.
    /// Returns true if the image was found and removed.
    /// </summary>
    public static async Task<bool> TryRemoveImageByNameAsync(
        string cbzPath,
        string imageName,
        ILogger? logger = null
    )
    {
        try
        {
            if (!File.Exists(cbzPath))
                return false;
            await using ZipArchive archive = await ZipFile.OpenAsync(
                cbzPath,
                ZipArchiveMode.Update
            );

            var entry = archive.GetEntry(imageName);
            if (entry == null)
            {
                // Try to find by name without path
                var nameOnly = Path.GetFileName(imageName);
                entry = archive.Entries.FirstOrDefault(e =>
                    Path.GetFileName(e.FullName) == nameOnly
                );
            }

            if (entry == null)
            {
                logger?.LogWarning("Image {ImageName} not found in {Cbz}", imageName, cbzPath);
                return false;
            }

            logger?.LogInformation(
                "Removing matching image from {Cbz}: {EntryFullName}",
                cbzPath,
                entry.FullName
            );
            entry.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Failed to remove image {ImageName} from {Cbz}",
                imageName,
                cbzPath
            );
            return false;
        }
    }

    /// <summary>
    /// Removes a specific image by base name (without extension) from a CBZ archive.
    /// This is useful when the file extension might have changed after processing (e.g., upscaling).
    /// Returns true if the image was found and removed.
    /// </summary>
    public static async Task<bool> TryRemoveImageByBaseNameAsync(
        string cbzPath,
        string imageName,
        ILogger? logger = null
    )
    {
        try
        {
            if (!File.Exists(cbzPath))
                return false;
            await using ZipArchive archive = await ZipFile.OpenAsync(
                cbzPath,
                ZipArchiveMode.Update
            );

            // Get the base name without extension from the original image
            var baseNameWithoutExt = Path.GetFileNameWithoutExtension(imageName);
            var directoryName = Path.GetDirectoryName(imageName);

            // Find an image entry that matches the base name but potentially has a different extension
            var entry = archive
                .Entries.Where(e => !string.IsNullOrEmpty(e.Name)) // skip directories
                .Where(e => ImageConstants.IsSupportedImageExtension(Path.GetExtension(e.FullName)))
                .FirstOrDefault(e =>
                {
                    var entryBaseName = Path.GetFileNameWithoutExtension(e.FullName);
                    var entryDirectory = Path.GetDirectoryName(e.FullName);

                    // Match both the base name and directory path
                    return string.Equals(
                            entryBaseName,
                            baseNameWithoutExt,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && string.Equals(
                            entryDirectory,
                            directoryName,
                            StringComparison.OrdinalIgnoreCase
                        );
                });

            if (entry == null)
            {
                // Fallback: try to find by base name only (ignore directory structure)
                entry = archive
                    .Entries.Where(e => !string.IsNullOrEmpty(e.Name))
                    .Where(e =>
                        ImageConstants.IsSupportedImageExtension(Path.GetExtension(e.FullName))
                    )
                    .FirstOrDefault(e =>
                        string.Equals(
                            Path.GetFileNameWithoutExtension(e.FullName),
                            baseNameWithoutExt,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
            }

            if (entry == null)
            {
                logger?.LogWarning(
                    "Image with base name {BaseImageName} not found in {Cbz}",
                    baseNameWithoutExt,
                    cbzPath
                );
                return false;
            }

            logger?.LogInformation(
                "Removing matching image by base name from {Cbz}: {EntryFullName} (original: {OriginalName})",
                cbzPath,
                entry.FullName,
                imageName
            );
            entry.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Failed to remove image with base name {BaseImageName} from {Cbz}",
                Path.GetFileNameWithoutExtension(imageName),
                cbzPath
            );
            return false;
        }
    }
}
