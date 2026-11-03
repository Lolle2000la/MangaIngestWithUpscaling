using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

public interface IOnnxUpscaleEngine
{
    Task UpscaleFileAsync(
        string inputPath,
        string outputPath,
        int scale,
        CompressionFormat format,
        int? quality,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Like <see cref="UpscaleFileAsync"/>, but the outer task completes as soon as inference is done
    /// and the GPU is free. The returned inner task completes once the output file is written.
    /// Callers can start the next page's inference before awaiting it. The inner task must always
    /// be awaited so encode failures are observed.
    /// </summary>
    Task<Task> UpscaleFileStagedAsync(
        string inputPath,
        string outputPath,
        int scale,
        CompressionFormat format,
        int? quality,
        CancellationToken cancellationToken
    );

    Task UpscaleCbzAsync(
        string inputCbzPath,
        string outputCbzPath,
        int scale,
        CompressionFormat format,
        int? quality,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    );
}
