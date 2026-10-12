using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Extracts a downloaded model archive into the models directory.
/// </summary>
public static class ModelPackageExtractor
{
    /// <summary>
    /// Prefix of the scratch directory, which must not look like a model (<c>*.onnx</c>) and must be
    /// dot-prefixed so nothing that walks the models directory mistakes it for one.
    /// </summary>
    internal const string ScratchDirectoryPrefix = ".extract-";

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="modelsDirectory"/>, storing each
    /// file under <see cref="ModelFileNames.Resolve(string, string?)"/>. The models directory stays
    /// flat, so a file the archive kept in a subdirectory is stored under its own name.
    /// <para>
    /// The fp16 and fp32 archives of a model family ship identical file names, so the rename is
    /// what lets both precisions be installed at once. Extraction goes through a scratch
    /// directory, so a failure part-way through cannot leave the models directory holding a file
    /// under the other precision's name, and an entry in the archive can never overwrite one.
    /// </para>
    /// </summary>
    public static void Extract(string zipPath, string modelsDirectory, string? precisionSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);

        Directory.CreateDirectory(modelsDirectory);

        // The scratch directory lives inside the models directory rather than under
        // Path.GetTempPath(): the final move then stays on one filesystem and is a metadata-only
        // rename. Across filesystems (models on a data mount, temp on tmpfs) File.Move degrades to
        // a copy+delete, so the file appears in the models directory non-atomically and a
        // concurrent verify or GetOrCreateSession can read a half-written .onnx.
        string scratch = ScratchDirectory(modelsDirectory);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, scratch, overwriteFiles: true);

            // The models directory is deliberately flat, so a nested entry has to be flattened
            // like a top-level one: walking the whole scratch tree (not just its top level) is
            // what keeps an archive that keeps its files in a subdirectory from having them
            // deleted with the scratch tree instead of installed. The packages shipped today are
            // flat, so that guard is theoretical; if an archive ever nests two entries under the
            // same file name, the overwriting move makes whichever is enumerated last win.
            // Materialised before the moves below: EnumerateFiles is lazy, and moving a file out of the
            // directory being walked mutates that directory — an entry can be skipped, and the caller's
            // verify then throws FileNotFoundException outside its retry loop. GetFiles snapshots the
            // listing first.
            foreach (
                string extracted in Directory.GetFiles(scratch, "*", SearchOption.AllDirectories)
            )
            {
                string target = Path.Combine(
                    modelsDirectory,
                    ModelFileNames.Resolve(Path.GetFileName(extracted), precisionSuffix)
                );
                File.Move(extracted, target, overwrite: true);
            }
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>
    /// A fresh scratch directory for an extraction into <paramref name="modelsDirectory"/>.
    /// <para>
    /// It is inside the models directory so the install move stays on one filesystem, and it carries
    /// a <see cref="ScratchDirectoryPrefix"/> and a <c>.onnx</c>-free name so the flat model
    /// enumeration (<c>Directory.GetFiles(modelsDirectory, "*.onnx")</c>) can never mistake a
    /// crashed run's leftover for a model. Exposed for tests, which assert both properties.
    /// </para>
    /// </summary>
    internal static string ScratchDirectory(string modelsDirectory) =>
        Path.Combine(modelsDirectory, $"{ScratchDirectoryPrefix}{Guid.NewGuid():N}");
}
