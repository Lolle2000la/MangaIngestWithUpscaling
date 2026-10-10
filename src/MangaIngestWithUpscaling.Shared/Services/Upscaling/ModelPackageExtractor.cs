using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Extracts a downloaded model archive into the models directory.
/// </summary>
public static class ModelPackageExtractor
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="modelsDirectory"/>, storing each
    /// file under <see cref="ModelFileNames.Resolve(string, string?)"/>.
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

            foreach (string extracted in Directory.GetFiles(scratch))
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
