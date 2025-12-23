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

    Task DownloadModelsIfNecessary(CancellationToken cancellationToken);
}

public sealed record UpscaleProgress(
    int? Total,
    int? Current,
    string? Phase,
    string? StatusMessage
);
