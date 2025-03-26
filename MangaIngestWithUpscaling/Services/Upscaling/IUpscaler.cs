using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.Upscaling;

public interface IUpscaler
{
    Task Upscale(string inputPath, string outputPath, UpscalerProfile profile, CancellationToken cancellationToken);
}
