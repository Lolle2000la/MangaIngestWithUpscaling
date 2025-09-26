using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

public interface IUpscalerJsonHandlingService
{
    Task<UpscalerProfileJsonDto?> ReadUpscalerJsonAsync(
        string cbzFilePath,
        CancellationToken cancellationToken
    );
    Task WriteUpscalerJsonAsync(
        string cbzFilePath,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    );
    Task WriteUpscalerJsonAsync(
        ZipArchive archive,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    );
}
