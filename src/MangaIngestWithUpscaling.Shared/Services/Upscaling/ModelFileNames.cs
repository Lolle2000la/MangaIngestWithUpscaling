using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// On-disk names of the shipped model files.
/// <para>
/// The fp16 and fp32 archives of a model family ship the same file names, so the two precisions
/// cannot be extracted into one directory without clobbering each other. The fp32 copies
/// therefore carry an explicit <see cref="Fp32Suffix"/>: a file name that already ends in a
/// precision marker has it replaced, anything else gets it appended.
/// </para>
/// <para>
/// Both precisions stay on disk, because whether a device can run a model is only known by
/// running it — the WebGPU EP executes the transformer models as an all-NaN tensor rather than
/// reporting a missing kernel, so the engine has to be able to fall back from the fp16 copy to
/// the fp32 one (see <see cref="OnnxUpscaleEngine.SelectModelCandidates"/>). On an accelerator
/// that supports fp16 the smaller file wins and the extra copy is never touched.
/// </para>
/// </summary>
public static partial class ModelFileNames
{
    /// <summary>Marks the fp32 copy of a model in the models directory.</summary>
    public const string Fp32Suffix = "_fp32";

    /// <summary>True when a file is the fp32 copy of a model.</summary>
    public static bool IsFp32(string fileName) =>
        Path.GetFileName(fileName).Contains(Fp32Suffix, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"_(fp16|fp32|bf16)$", RegexOptions.CultureInvariant)]
    private static partial Regex PrecisionMarker();

    /// <summary>
    /// The name a file from an archive is stored under. <paramref name="precisionSuffix"/> is
    /// null for the precision whose files keep their shipped names (the fp16 set).
    /// </summary>
    public static string Resolve(string archiveFileName, string? precisionSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFileName);

        if (precisionSuffix is null)
        {
            return Path.GetFileName(archiveFileName);
        }

        string core = PrecisionMarker()
            .Replace(Path.GetFileNameWithoutExtension(archiveFileName), "");
        return core + precisionSuffix + Path.GetExtension(archiveFileName);
    }
}
