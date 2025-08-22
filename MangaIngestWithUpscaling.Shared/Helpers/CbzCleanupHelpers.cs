using Microsoft.Extensions.Logging;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Helpers;

public static class CbzCleanupHelpers
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".avif"
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
}
