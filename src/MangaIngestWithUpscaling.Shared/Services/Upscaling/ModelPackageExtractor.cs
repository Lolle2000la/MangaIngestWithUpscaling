using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Extracts a downloaded model archive into the models directory.
/// </summary>
public static class ModelPackageExtractor
{
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

        string scratch = Path.Combine(Path.GetTempPath(), $"model_extract_{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, scratch, overwriteFiles: true);

            // The models directory is deliberately flat, so a nested entry has to be flattened
            // like a top-level one: walking the whole scratch tree (not just its top level) is
            // what keeps an archive that keeps its files in a subdirectory from having them
            // deleted with the scratch tree instead of installed. The packages shipped today are
            // flat, so that guard is theoretical; if an archive ever nests two entries under the
            // same file name, the overwriting move makes whichever is enumerated last win.
            foreach (
                string extracted in Directory.EnumerateFiles(
                    scratch,
                    "*",
                    SearchOption.AllDirectories
                )
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
}
