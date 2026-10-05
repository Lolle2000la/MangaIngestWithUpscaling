using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using SharedCompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using SharedUpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Builds the page descriptors and identities that drive page streaming: which source entries make up
/// a chapter, the output name each maps to, and the hash that pins a streamed run to its inputs.
/// </summary>
public static class PageManifestBuilder
{
    internal static List<SpoolPageDescriptor> BuildPageDescriptors(
        string sourcePath,
        SharedUpscalerProfile profile
    )
    {
        string extension = FormatExtension(profile.CompressionFormat);
        var pages = new List<SpoolPageDescriptor>();
        using ZipArchive archive = ZipFile.OpenRead(sourcePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.FullName)))
            {
                continue;
            }

            // Reject names that could escape the output archive on extraction.
            if (!PageStreamSpool.IsSafeEntryName(entry.FullName))
            {
                continue;
            }

            // A malformed archive can repeat an entry name; keep the first so the spool's
            // source-name mapping stays unique.
            if (!seen.Add(entry.FullName))
            {
                continue;
            }

            // Preserve the entry's folder (matching the whole-CBZ worker) so nested pages are not
            // flattened and same-stemmed pages in different folders do not collide.
            string outputName = ReplaceExtension(entry.FullName, extension);

            // Two entries in one folder can share a stem (001.jpg + 001.png) and thus an output
            // name; the whole-CBZ path silently overwrites one, so disambiguate and keep both pages.
            if (!outputNames.Add(outputName))
            {
                string suffix = $".{extension}";
                string stem = outputName[..^suffix.Length];
                int n = 1;
                string candidate;
                do
                {
                    candidate = $"{stem}_{n++}{suffix}";
                } while (!outputNames.Add(candidate));
                outputName = candidate;
            }

            pages.Add(new SpoolPageDescriptor(index, entry.FullName, outputName));
            index++;
        }

        return pages;
    }

    /// <summary>
    /// Replaces an archive entry's extension while preserving its folder prefix, e.g.
    /// "ch/005.jpg" with "webp" becomes "ch/005.webp".
    /// </summary>
    private static string ReplaceExtension(string entryName, string extension)
    {
        string current = Path.GetExtension(entryName);
        return current.Length == 0
            ? $"{entryName}.{extension}"
            : entryName[..^current.Length] + "." + extension;
    }

    internal static List<SpoolPageDescriptor> BuildRepairPageDescriptors(
        string sourcePath,
        IReadOnlyList<string> missingStems,
        SharedUpscalerProfile profile
    )
    {
        string extension = FormatExtension(profile.CompressionFormat);
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                if (!ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.FullName)))
                {
                    continue;
                }

                if (!PageStreamSpool.IsSafeEntryName(entry.FullName))
                {
                    continue;
                }

                byStem.TryAdd(Path.GetFileNameWithoutExtension(entry.FullName), entry.FullName);
            }
        }

        var pages = new List<SpoolPageDescriptor>();
        int index = 0;
        foreach (string stem in missingStems)
        {
            if (byStem.TryGetValue(stem, out string? sourceName))
            {
                // Repair flattens to the stem: the merge (RepairService.MergeRepairResults) copies
                // top-level files by name, and the whole-CBZ repair path flattens too. Preserving
                // the source folder here would make the merged page invisible and silently no-op.
                pages.Add(new SpoolPageDescriptor(index, sourceName, $"{stem}.{extension}"));
                index++;
            }
        }

        return pages;
    }

    internal static string ComputeRepairIdentity(
        string sourcePath,
        string upscaledPath,
        SharedUpscalerProfile profile,
        IReadOnlyList<string> missingPages,
        ImagePreprocessingOptions preprocessing
    )
    {
        FileInfo source = new(sourcePath);
        FileInfo upscaled = new(upscaledPath);
        string material = string.Join(
            '|',
            "repair",
            sourcePath,
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            upscaledPath,
            upscaled.Length,
            upscaled.LastWriteTimeUtc.Ticks,
            profile.Id,
            (int)profile.CompressionFormat,
            (int)profile.ScalingFactor,
            profile.Quality,
            (int)profile.UpscalerMethod,
            string.Join(',', missingPages.OrderBy(p => p, StringComparer.Ordinal)),
            PreprocessingFingerprint(preprocessing)
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// A stable fingerprint of the effective preprocessing. The server owns these options and sends
    /// them to the worker, so they belong in the server-computed content identity: a change to them
    /// must discard the spool (otherwise pages preprocessed differently could be assembled into one
    /// chapter). The worker's engine identity deliberately excludes them.
    /// </summary>
    internal static string PreprocessingFingerprint(ImagePreprocessingOptions options)
    {
        var material = new StringBuilder();
        material
            .Append(
                options.MaxDimension is null or 0
                    ? "-"
                    : options.MaxDimension.Value.ToString(CultureInfo.InvariantCulture)
            )
            .Append('|')
            .Append(options.EnableSmartDownscale)
            .Append('|');
        if (options.EnableSmartDownscale)
        {
            material
                .Append(options.SmartDownscaleThreshold.ToString("R", CultureInfo.InvariantCulture))
                .Append('|')
                .Append(options.SmartDownscaleFactor.ToString("R", CultureInfo.InvariantCulture))
                .Append('|');
        }

        foreach (ImageFormatConversionRule rule in options.FormatConversionRules ?? [])
        {
            material
                .Append(rule.FromFormat)
                .Append('>')
                .Append(rule.ToFormat)
                .Append('@')
                .Append(rule.Quality?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append(',');
        }

        return material.ToString();
    }

    internal static List<SpoolPageDescriptor> BuildDetectionPageDescriptors(string sourcePath)
    {
        var pages = new List<SpoolPageDescriptor>();
        using ZipArchive archive = ZipFile.OpenRead(sourcePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.FullName)))
            {
                continue;
            }

            if (!PageStreamSpool.IsSafeEntryName(entry.FullName))
            {
                continue;
            }

            if (!seen.Add(entry.FullName))
            {
                continue;
            }

            pages.Add(new SpoolPageDescriptor(index, entry.FullName, entry.FullName));
            index++;
        }

        return pages;
    }

    internal static string ComputeDetectionIdentity(string sourcePath, int detectorVersion)
    {
        FileInfo info = new(sourcePath);
        string material = string.Join(
            '|',
            "detect",
            sourcePath,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            detectorVersion
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    internal static string ComputeIdentity(
        string sourcePath,
        SharedUpscalerProfile profile,
        ImagePreprocessingOptions preprocessing
    )
    {
        FileInfo info = new(sourcePath);
        string material = string.Join(
            '|',
            sourcePath,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            profile.Id,
            (int)profile.CompressionFormat,
            (int)profile.ScalingFactor,
            profile.Quality,
            (int)profile.UpscalerMethod,
            PreprocessingFingerprint(preprocessing)
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    internal static string FormatExtension(SharedCompressionFormat format) =>
        format switch
        {
            SharedCompressionFormat.Avif => "avif",
            SharedCompressionFormat.Png => "png",
            SharedCompressionFormat.Webp => "webp",
            SharedCompressionFormat.Jpg => "jpeg",
            _ => "webp",
        };
}
