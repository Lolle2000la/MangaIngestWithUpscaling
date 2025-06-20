using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

public interface IUpscaler
{
    Task Upscale(string inputPath, string outputPath, UpscalerProfile profile, CancellationToken cancellationToken);
    Task DownloadModelsIfNecessary(CancellationToken cancellationToken);
}