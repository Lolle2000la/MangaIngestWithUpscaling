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
/// different engine, so a chapter cannot be assembled from pages upscaled by different models, engine
/// code or backend (which would leave a visible seam). The server never interprets the value.
/// </summary>
public static class EngineIdentity
{
    /// <summary>
    /// Version of the upscaler engine. Increment when engine implementation changes. It must be
    /// hashed explicitly so changes invalidate cached engine spools across workers.
    /// </summary>
    public const int CurrentEngineVersion = 1;

    /// <summary>
    /// Identity of the upscaler: the effective compute mode (CPU vs GPU) and accelerator backend, the
    /// engine code version, the app build, and the model files under
    /// <see cref="UpscalerConfig.ModelsDirectory"/>. Preprocessing is deliberately excluded: the
    /// server owns it and folds the effective options into the content identity instead, so the worker
    /// hashes only what it controls. The specific GPU index is deliberately excluded so a hand-off
    /// between two GPUs of the same backend keeps the spool; that assumes the same models produce the
    /// same pixels on every device of a backend. Models are fingerprinted by relative path, size and a
    /// 64 KiB sample from the head, middle and tail of each file — stable across workers with identical
    /// models and far cheaper than hashing the ~gigabytes of weights, while still distinguishing a
    /// same-size fine-tune that diverges past the first window.
    /// </summary>
    /// <param name="resolvedBackend">
    /// The accelerator backend actually resolved and in use (e.g. from <c>IOnnxSessionFactory.GetEffectiveBackend()</c>).
    /// Hashed instead of <see cref="UpscalerConfig.PreferredGpuBackend"/> because the preference defaults to
    /// <see cref="GpuBackend.Auto"/>: two default deployments on different hardware would otherwise hash the same
    /// and be allowed to blend pages. Falls back to the preference when the backend has not been probed yet.
    /// </param>
    /// <param name="engineVersion">
    /// Overrides <see cref="CurrentEngineVersion"/>; intended for tests. Production callers omit it so
    /// bumping the constant invalidates every chapter's spool.
    /// </param>
    public static string ForUpscaler(
        UpscalerConfig config,
        GpuBackend? resolvedBackend = null,
        int? engineVersion = null
    )
    {
        var material = new StringBuilder("upscaler|");
        // Preprocessing (max dimension, format conversion, smart downscale) is deliberately NOT hashed
        // here. The server owns it and sends the effective options to the worker, which applies them;
        // the server folds them into the content identity instead, so a preprocessing change resets
        // the spool there. Hashing the worker's local copy would be wrong in both directions: it would
        // miss a server-side change and spuriously reject a worker whose local config differs.
        material
            .Append(config.ResolvedUseFp16)
            .Append('|')
            // The effective compute mode, not just UseCPU: the worker maps
            // SelectedDeviceIndex = UseCPU ? 0 : SelectedDeviceIndex, and device 0 is the CPU switch.
            // So UseCPU=true and (UseCPU=false, SelectedDeviceIndex=0) must hash the same and differ
            // from a GPU run (SelectedDeviceIndex > 0).
            .Append(config.UseCPU || config.SelectedDeviceIndex == 0)
            .Append('|')
            // The *resolved* accelerator backend, so a CUDA and a WebGPU run never mix even when both
            // are configured Auto. The GPU index itself is not hashed, so a same-backend hand-off
            // keeps the spool.
            .Append(resolvedBackend ?? config.PreferredGpuBackend)
            .Append('|')
            // The engine code version: changes to engine execution code can be bumped explicitly
            // or two different engines would hash the same.
            .Append(engineVersion ?? CurrentEngineVersion)
            .Append('|')
            .Append(BuildVersion())
            .Append('|');
        AppendDirectoryFingerprint(material, config.ResolvedModelsDirectory);

        return Hash(material.ToString());
    }

    /// <summary>
    /// Identity of the page-break detector: its version plus a content hash of the ONNX model file.
    /// </summary>
    public static string ForDetector(string? modelsDirectory = null)
    {
        var material = new StringBuilder("detector|");
        material.Append(SplitDetectionService.CURRENT_DETECTOR_VERSION).Append('|');
        string modelPath = SplitDetectionLayout.ResolveModelPath(modelsDirectory);
        AppendFileContentHash(material, modelPath);
        return Hash(material.ToString());
    }

    private static void AppendDirectoryFingerprint(StringBuilder material, string directory)
    {
        if (!Directory.Exists(directory))
        {
            material.Append("missing");
            return;
        }

        // Normalize separators and casing so the fingerprint agrees across operating systems (two
        // workers with the same models must not discard the spool), and sort by the normalized
        // relative path so enumeration order does not matter.
        IEnumerable<(string Relative, long Length, string File)> files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file =>
                (
                    Relative: Path.GetRelativePath(directory, file)
                        .Replace('\\', '/')
                        .ToLowerInvariant(),
                    Length: new FileInfo(file).Length,
                    File: file
                )
            )
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal);
        foreach ((string relative, long length, string file) in files)
        {
            material.Append(relative).Append(':').Append(length).Append(':');
            AppendFilePrefixHash(material, file);
            material.Append(';');
        }
    }

    /// <summary>
    /// Bytes sampled from each of the head, middle and tail of a model file for the fingerprint. A
    /// full content hash of multi-gigabyte weights is too slow, but path + size alone collide for a
    /// same-size fine-tune; sampling three windows distinguishes those cheaply (the result is cached
    /// per process by the provider).
    /// </summary>
    private const int FingerprintSampleBytes = 64 * 1024;

    private static void AppendFilePrefixHash(StringBuilder material, string file)
    {
        // Let an unreadable file throw: the identity provider only caches a successful computation, so
        // a transient lock/ACL error is retried on the next access instead of becoming a stable-but-
        // wrong identity that rejects every page forever.
        using FileStream stream = File.OpenRead(file);
        long length = stream.Length;

        // Sample the head, middle and tail, so a same-size fine-tune that diverges past the first
        // window is still distinguished. The tail is sampled for any file longer than one window
        // (not only longer than two): a 64-128 KiB file otherwise sampled head+middle only, so a
        // fine-tune differing solely in its tail hashed identically. The middle is skipped when it
        // would duplicate the head (a file only just over one window long).
        var offsets = new List<long> { 0 };
        if (length > FingerprintSampleBytes)
        {
            long middle = (length - FingerprintSampleBytes) / 2;
            if (middle > 0)
            {
                offsets.Add(middle);
            }

            offsets.Add(length - FingerprintSampleBytes);
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[FingerprintSampleBytes];
        foreach (long offset in offsets)
        {
            stream.Position = offset;
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            hasher.AppendData(buffer, 0, read);
        }

        material.Append(Convert.ToHexStringLower(hasher.GetHashAndReset()));
    }

    private static void AppendFileContentHash(StringBuilder material, string path)
    {
        material.Append(Path.GetFileName(path).ToLowerInvariant()).Append(':');
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
