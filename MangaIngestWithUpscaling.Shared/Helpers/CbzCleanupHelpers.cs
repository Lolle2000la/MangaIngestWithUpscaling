using Microsoft.Extensions.Logging;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Helpers;

public static class CbzCleanupHelpers
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".avif"
    };

    /// <summary>
    /// Removes a single odd-one-out image type from a CBZ archive, when all other images share a different extension.
    /// Returns true if a file was removed.
    /// </summary>
    public static bool TryRemoveOddOneOutImage(string cbzPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(cbzPath)) return false;
            using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Update);

            // Collect image entries (files only)
            var images = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name)) // skip directories
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .ToList();

            if (images.Count < 2)
            {
                return false; // nothing to do
            }

            var groups = images
                .GroupBy(e => Path.GetExtension(e.FullName).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            if (groups.Count != 2)
            {
                return false; // only one type present
            }

            // Majority must be of a single type, and exactly one image must be of a different type.
            // Determine the majority group and sum of all others.
            var majority = groups.OrderByDescending(kv => kv.Value.Count).First();
            int othersCount = images.Count - majority.Value.Count;
            if (othersCount != 1)
            {
                return false; // not exactly one odd image
            }

            // Identify the odd image (the only member of a non-majority group)
            var oddGroup = groups.Where(kv => kv.Key != majority.Key).First(kv => kv.Value.Count == 1);

            var oddEntry = oddGroup.Value[0];

            logger?.LogInformation(
                "Removing odd-one-out image from {Cbz}: {EntryFullName} (ext {Ext}), majority ext: {MajorityExt}",
                cbzPath, oddEntry.FullName, Path.GetExtension(oddEntry.FullName), majority.Key);

            oddEntry.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to cleanup odd-one-out image from {Cbz}", cbzPath);
            return false;
        }
    }

    /// <summary>
    /// Removes a single odd-one-out image type from a CBZ archive, when all other images share a different extension.
    /// Returns the name of the removed image, or null if no image was removed.
    /// </summary>
    public static string? TryRemoveOddOneOutImageAndGetName(string cbzPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(cbzPath)) return null;
            using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Update);

            // Collect image entries (files only)
            var images = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name)) // skip directories
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .ToList();

            if (images.Count < 2)
            {
                return null; // nothing to do
            }

            var groups = images
                .GroupBy(e => Path.GetExtension(e.FullName).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            if (groups.Count != 2)
            {
                return null; // only one type present
            }

            // Majority must be of a single type, and exactly one image must be of a different type.
            // Determine the majority group and sum of all others.
            var majority = groups.OrderByDescending(kv => kv.Value.Count).First();
            int othersCount = images.Count - majority.Value.Count;
            if (othersCount != 1)
            {
                return null; // not exactly one odd image
            }

            // Identify the odd image (the only member of a non-majority group)
            var oddGroup = groups.Where(kv => kv.Key != majority.Key).First(kv => kv.Value.Count == 1);

            var oddEntry = oddGroup.Value[0];
            var removedImageName = oddEntry.FullName;

            logger?.LogInformation(
                "Removing odd-one-out image from {Cbz}: {EntryFullName} (ext {Ext}), majority ext: {MajorityExt}",
                cbzPath, oddEntry.FullName, Path.GetExtension(oddEntry.FullName), majority.Key);

            oddEntry.Delete();
            return removedImageName;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to cleanup odd-one-out image from {Cbz}", cbzPath);
            return null;
        }
    }

    /// <summary>
    /// Removes a specific image by exact name from a CBZ archive.
    /// Returns true if the image was found and removed.
    /// </summary>
    public static bool TryRemoveImageByName(string cbzPath, string imageName, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(cbzPath)) return false;
            using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Update);

            // First try to find by exact path match
            var entry = archive.GetEntry(imageName);

            if (entry == null)
            {
                logger?.LogWarning("Image {ImageName} not found in {Cbz} - will not remove any files to prevent false positives", imageName, cbzPath);
                return false;
            }

            logger?.LogInformation("Removing matching image from {Cbz}: {EntryFullName}", cbzPath, entry.FullName);
            entry.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to remove image {ImageName} from {Cbz}", imageName, cbzPath);
            return false;
        }
    }

    /// <summary>
    /// Removes a specific image by base name (without extension) from a CBZ archive.
    /// This is useful when the file extension might have changed after processing (e.g., upscaling).
    /// Returns true if the image was found and removed.
    /// </summary>
    public static bool TryRemoveImageByBaseName(string cbzPath, string imageName, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(cbzPath)) return false;
            using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Update);

            // Get the base name without extension from the original image
            var baseNameWithoutExt = Path.GetFileNameWithoutExtension(imageName);
            var directoryName = Path.GetDirectoryName(imageName);

            // Find an image entry that matches the base name but potentially has a different extension
            var entry = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name)) // skip directories
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .FirstOrDefault(e =>
                {
                    var entryBaseName = Path.GetFileNameWithoutExtension(e.FullName);
                    var entryDirectory = Path.GetDirectoryName(e.FullName);

                    // Match both the base name and directory path
                    return string.Equals(entryBaseName, baseNameWithoutExt, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(entryDirectory, directoryName, StringComparison.OrdinalIgnoreCase);
                });

            if (entry == null)
            {
                // Fallback: try to find by base name only (ignore directory structure)
                entry = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                    .FirstOrDefault(e =>
                        string.Equals(Path.GetFileNameWithoutExtension(e.FullName), baseNameWithoutExt,
                            StringComparison.OrdinalIgnoreCase));
            }

            if (entry == null)
            {
                logger?.LogWarning("Image with base name {BaseImageName} not found in {Cbz}", baseNameWithoutExt,
                    cbzPath);
                return false;
            }

            logger?.LogInformation(
                "Removing matching image by base name from {Cbz}: {EntryFullName} (original: {OriginalName})",
                cbzPath, entry.FullName, imageName);
            entry.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to remove image with base name {BaseImageName} from {Cbz}",
                Path.GetFileNameWithoutExtension(imageName), cbzPath);
            return false;
        }
    }

    /// <summary>
    /// Finds a single odd-one-out image type in a CBZ archive, when all other images share a different extension.
    /// Returns the full name of the odd image, or null if no odd image was found.
    /// </summary>
    public static string? FindOddOneOutImage(string cbzPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(cbzPath)) return null;
            using var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Read);

            // Collect image entries (files only)
            var images = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name)) // skip directories
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .ToList();

            if (images.Count < 2)
            {
                return null; // nothing to do
            }

            var groups = images
                .GroupBy(e => Path.GetExtension(e.FullName).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            if (groups.Count != 2)
            {
                return null; // only one type present
            }

            // Majority must be of a single type, and exactly one image must be of a different type.
            // Determine the majority group and sum of all others.
            var majority = groups.OrderByDescending(kv => kv.Value.Count).First();
            int othersCount = images.Count - majority.Value.Count;
            if (othersCount != 1)
            {
                return null; // not exactly one odd image
            }

            // Identify the odd image (the only member of a non-majority group)
            var oddGroup = groups.Where(kv => kv.Key != majority.Key).First(kv => kv.Value.Count == 1);
            var oddEntry = oddGroup.Value[0];

            logger?.LogInformation(
                "Found odd-one-out image in {Cbz}: {EntryFullName} (ext {Ext}), majority ext: {MajorityExt}",
                cbzPath, oddEntry.FullName, Path.GetExtension(oddEntry.FullName), majority.Key);

            return oddEntry.FullName;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to find odd-one-out image in {Cbz}", cbzPath);
            return null;
        }
    }
}