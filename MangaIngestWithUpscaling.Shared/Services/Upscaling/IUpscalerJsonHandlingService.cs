using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

public interface IUpscalerJsonHandlingService
{
    Task<UpscalerProfileJsonDto?> ReadUpscalerJsonAsync(string cbzFilePath, CancellationToken cancellationToken);
    Task WriteUpscalerJsonAsync(string cbzFilePath, UpscalerProfile profile, CancellationToken cancellationToken);
    Task WriteUpscalerJsonAsync(ZipArchive archive, UpscalerProfile profile, CancellationToken cancellationToken);
}