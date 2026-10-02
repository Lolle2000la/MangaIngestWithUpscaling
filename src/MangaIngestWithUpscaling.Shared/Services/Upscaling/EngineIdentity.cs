using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Analysis;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Computes an opaque, worker-supplied identity for the engine that will produce a chapter's pages.
/// The server records the first identity a chapter is spooled with and refuses pages produced by a
/// different engine, so a chapter cannot be assembled from pages upscaled by different models or
/// preprocessing (which would leave a visible seam). The server never interprets the value.
/// </summary>
public static class EngineIdentity
{
    /// <summary>
    /// Identity of the upscaler: the preprocessing configuration, the effective compute mode (CPU vs
    /// GPU), the app/engine build, the resolved workflow (appstate2.json) and the model files under
    /// <see cref="UpscalerConfig.ModelsDirectory"/>. Which GPU is selected is deliberately excluded:
    /// a CUDA box and a ROCm box with the same models and preprocessing produce the same pixels, so a
    /// cross-device hand-off must not discard the spool. Models are fingerprinted by relative path and
    /// size — stable across workers with identical models and far cheaper than hashing the
    /// ~gigabytes of weights.
    /// </summary>
    public static string ForUpscaler(UpscalerConfig config)
    {
        var material = new StringBuilder("upscaler|");
        material
            .Append(
                config.MaxDimensionBeforeUpscaling?.ToString(CultureInfo.InvariantCulture) ?? "-"
            )
            .Append('|')
            .Append(config.EnableSmartDownscale)
            .Append('|')
            .Append(config.SmartDownscaleThreshold.ToString("R", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(config.SmartDownscaleFactor.ToString("R", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(config.UseFp16)
            .Append('|')
            // The effective compute mode, not just UseCPU: the worker maps
            // SelectedDeviceIndex = UseCPU ? 0 : SelectedDeviceIndex, and device 0 is the CPU switch.
            // So UseCPU=true and (UseCPU=false, SelectedDeviceIndex=0) must hash the same and differ
            // from a GPU run (SelectedDeviceIndex > 0).
            .Append(config.UseCPU || config.SelectedDeviceIndex == 0)
            .Append('|');

        foreach (ImageFormatConversionRule rule in config.ImageFormatConversionRules)
        {
            material
                .Append(rule.FromFormat)
                .Append('>')
                .Append(rule.ToFormat)
                .Append('@')
                .Append(rule.Quality?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append(',');
        }

        material.Append('|').Append(BuildVersion()).Append('|');
        AppendFileContentHash(material, Path.Combine(AppContext.BaseDirectory, "appstate2.json"));

        material.Append('|');
        AppendDirectoryFingerprint(material, config.ModelsDirectory);

        return Hash(material.ToString());
    }

    /// <summary>
    /// Identity of the page-break detector: its version plus a content hash of the bundled checkpoint
    /// and config files (small enough to hash exactly).
    /// </summary>
    public static string ForDetector()
    {
        var material = new StringBuilder("detector|");
        material.Append(SplitDetectionService.CURRENT_DETECTOR_VERSION).Append('|');
        AppendFileContentHash(material, SplitDetectionLayout.CheckpointPath);
        AppendFileContentHash(material, SplitDetectionLayout.ConfigPath);
        return Hash(material.ToString());
    }

    private static void AppendDirectoryFingerprint(StringBuilder material, string directory)
    {
        if (!Directory.Exists(directory))
        {
            material.Append("missing");
            return;
        }

        // Sorted so the fingerprint is deterministic regardless of enumeration order.
        IEnumerable<string> files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (string file in files)
        {
            material
                .Append(Path.GetRelativePath(directory, file))
                .Append(':')
                .Append(new FileInfo(file).Length)
                .Append(';');
        }
    }

    private static void AppendFileContentHash(StringBuilder material, string path)
    {
        material.Append(Path.GetFileName(path)).Append(':');
        if (File.Exists(path))
        {
            using FileStream stream = File.OpenRead(path);
            material.Append(Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        else
        {
            material.Append("missing");
        }

        material.Append('|');
    }

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    /// <summary>The build of the converter, so a changed build invalidates a chapter's spool.</summary>
    private static string BuildVersion() =>
        typeof(EngineIdentity)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(EngineIdentity).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
