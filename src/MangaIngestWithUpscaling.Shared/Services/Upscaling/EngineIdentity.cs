using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

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
    /// <para>
    /// The value is not a gate on its own: the identity also hashes the build version and the
    /// backend, so the whole engine change that came with this migration is already distinguished
    /// without bumping this. It exists for a future change that keeps the build version and the
    /// backend the same — and then it is the only thing that can separate them.
    /// </para>
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
    /// It also selects which backend's FP16 answer is hashed when <see cref="UpscalerConfig.UseFp16"/> is unset.
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
            // The precision the model set is actually chosen from. With UseFp16 set, the operator's
            // explicit choice wins; with it unset, the probe answers for the *resolved* backend,
            // exactly as MangaJaNaiUpscaler.GetModelPackages resolves it for the download. Hashing
            // the preference's answer instead (ResolvedUseFp16 resolves against PreferredGpuBackend)
            // would describe a model set that was never downloaded whenever Auto resolved elsewhere.
            .Append(
                config.UseFp16
                    ?? config.ResolveUseFp16(resolvedBackend ?? config.PreferredGpuBackend)
            )
            .Append('|')
            // The effective compute mode, not just UseCPU: device 0 is the CPU switch, and
            // PreferredGpuBackend = CPU is the third way to ask for CPU. ShouldUseCpuProvider is the
            // single place that decides it, so this defers to it rather than restating the rule — the
            // two must agree or two workers with equivalent settings reject each other's spool.
            .Append(OnnxSessionFactory.ShouldUseCpuProvider(config))
            .Append('|')
            // The *resolved* accelerator backend, so a CUDA and a WebGPU run never mix even when both
            // are configured Auto. The GPU index itself is not hashed, so a same-backend hand-off
            // keeps the spool.
            .Append(resolvedBackend ?? config.PreferredGpuBackend)
            .Append('|')
            // The architecture preference order, because it decides *which weights run* for a page
            // (OnnxUpscaleEngine.SelectModelCandidates): two workers with the same models directory
            // but a different order produce different pixels. It is worker-local configuration and
            // does not travel in the job request, so without hashing it here the server would happily
            // assemble one chapter out of both — the seam this identity exists to prevent. The
            // resolved order is hashed, not the raw string, so an equivalent spelling
            // ("fdat_xl,FDAT_M" and "FDAT_XL,FDAT_M") is the same engine.
            .Append(
                string.Join(
                    ',',
                    ModelArchitecturePreference.Resolve(config.PreferredModelArchitectures)
                )
            )
            .Append('|')
            // The engine code version: changes to engine execution code can be bumped explicitly
            // or two different engines would hash the same.
            .Append(engineVersion ?? CurrentEngineVersion)
            .Append('|')
            .Append(BuildVersion())
            .Append('|')
            .Append(RuntimeVersion())
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

        string detectorFileName = Path.GetFileName(
            SplitDetectionLayout.ResolveModelPath(directory)
        );

        // Only the ONNX weights go in. The same directory also holds the per-device calibration
        // profile (device-memory-profile.json) and a .verified_* marker per downloaded package, and
        // both change on a worker without the models changing at all — the profile is written the
        // first time a device is calibrated, which is after an identity was typically already
        // computed. Hashing those would give two workers with byte-identical models different
        // identities, which is exactly what this fingerprint promises not to do.
        // Normalize separators and casing so the fingerprint agrees across operating systems (two
        // workers with the same models must not discard the spool), and sort by the normalized
        // relative path so enumeration order does not matter.
        IEnumerable<(string Relative, long Length, string File)> files = Directory
            .EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories)
            .Select(file =>
                (
                    Relative: Path.GetRelativePath(directory, file)
                        .Replace('\\', '/')
                        .ToLowerInvariant(),
                    Length: new FileInfo(file).Length,
                    File: file
                )
            )
            // Nothing from a dot-directory: the extractor works in a `.extract-<guid>` scratch inside
            // this directory, so a live or crashed one would otherwise contribute duplicate weights to
            // the identity even though the installed models are unchanged.
            .Where(entry => !entry.Relative.Split('/').Any(segment => segment.StartsWith('.')))
            // The page-break detector lives in this directory too but is hashed separately by
            // ForDetector, so a detector-only change must not invalidate an upscale spool.
            .Where(entry =>
                !string.Equals(
                    Path.GetFileName(entry.File),
                    detectorFileName,
                    StringComparison.OrdinalIgnoreCase
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

    /// <summary>
    /// The ONNX Runtime the weights are actually executed by: the runtime assembly and the WebGPU
    /// provider library that ships beside it.
    /// <para>
    /// <see cref="BuildVersion"/> is this repository's own version and does not move when the runtime
    /// does — <c>OnnxRuntimeVersion</c> and <c>OnnxRuntimeFlavor</c> are build properties that swap the
    /// bundled native library while leaving <c>InformationalVersion</c> untouched. Two workers on the
    /// same backend but different runtimes can still turn the same graph into different pixels (kernel
    /// selection and fp16 handling both differ between builds), which is the same seam the backend and
    /// the weight fingerprint are hashed for.
    /// </para>
    /// <para>
    /// Both versions come from compile-time type references rather than from the list of loaded
    /// assemblies, whose contents depend on which provider has been touched already and would give two
    /// equivalent workers different identities.
    /// </para>
    /// </summary>
    private static string RuntimeVersion() =>
        $"{AssemblyVersion(typeof(InferenceSession))}+{AssemblyVersion(typeof(WebGpuEp))}";

    private static string AssemblyVersion(Type type) =>
        type.Assembly.GetName().Version?.ToString() ?? "unknown";
}
