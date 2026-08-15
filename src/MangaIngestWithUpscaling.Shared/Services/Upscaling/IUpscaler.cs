using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

public interface IUpscaler
{
    Task Upscale(
        string inputPath,
        string outputPath,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    );

    Task Upscale(
        string inputPath,
        string outputPath,
        UpscalerProfile profile,
        IProgress<UpscaleProgress> progress,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Preprocesses the input (resize/format conversion) if configured and returns a disposable
    /// handle to the archive that should be fed to <see cref="UpscalePreprocessedAsync"/>. The
    /// handle is a no-op wrapper around the original path when preprocessing is not needed.
    /// </summary>
    Task<IPreprocessedInput> PreprocessAsync(
        string inputPath,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Upscales a previously preprocessed input to the output path.
    /// </summary>
    Task UpscalePreprocessedAsync(
        IPreprocessedInput preprocessed,
        string outputPath,
        UpscalerProfile profile,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    );

    Task DownloadModelsIfNecessary(CancellationToken cancellationToken);
}

/// <summary>
/// Disposable handle to the archive (temporary preprocessed CBZ, or the original path) that
/// should be upscaled. Disposing releases the temporary file when one was created.
/// </summary>
public interface IPreprocessedInput : IDisposable
{
    string InputPath { get; }
}

public sealed record UpscaleProgress(
    int? Total,
    int? Current,
    string? Phase,
    string? StatusMessage
);
