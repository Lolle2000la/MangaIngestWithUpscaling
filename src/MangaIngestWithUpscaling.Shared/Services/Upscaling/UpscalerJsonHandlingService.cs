using System.IO.Compression;
using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterScoped]
public class UpscalerJsonHandlingService(ILogger<UpscalerJsonHandlingService> logger)
    : IUpscalerJsonHandlingService
{
    public async Task<UpscalerProfileJsonDto?> ReadUpscalerJsonAsync(
        string cbzFilePath,
        CancellationToken cancellationToken
    )
    {
        if (!cbzFilePath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            await using ZipArchive archive = await ZipFile.OpenReadAsync(
                cbzFilePath,
                cancellationToken
            );
            ZipArchiveEntry? upscalerJsonEntry = archive.GetEntry("upscaler.json");
            if (upscalerJsonEntry != null)
            {
                await using Stream stream = await upscalerJsonEntry.OpenAsync(cancellationToken);
                UpscalerProfileJsonDto? upscalerProfileDto = await JsonSerializer.DeserializeAsync(
                    stream,
                    UpscalerJsonContext.Default.UpscalerProfileJsonDto,
                    cancellationToken
                );
                return upscalerProfileDto;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking for upscaler.json in {Path}", cbzFilePath);
        }

        return null;
    }

    public async Task WriteUpscalerJsonAsync(
        string cbzFilePath,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    )
    {
        await using ZipArchive archive = await ZipFile.OpenAsync(
            cbzFilePath,
            ZipArchiveMode.Update,
            cancellationToken
        );
        await WriteUpscalerJsonAsync(archive, profile, cancellationToken);
    }

    public async Task WriteUpscalerJsonAsync(
        ZipArchive archive,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    )
    {
        var upscalerJson = new UpscalerProfileJsonDto
        {
            Name = profile.Name,
            UpscalerMethod = profile.UpscalerMethod,
            ScalingFactor = (int)profile.ScalingFactor,
            CompressionFormat = profile.CompressionFormat,
            Quality = profile.Quality,
        };

        string jsonString = JsonSerializer.Serialize(
            upscalerJson,
            UpscalerJsonContext.Default.UpscalerProfileJsonDto
        );

        // For Update mode, delete existing entry first
        if (archive.Mode != ZipArchiveMode.Create)
        {
            ZipArchiveEntry? existingEntry = archive.GetEntry("upscaler.json");
            existingEntry?.Delete();
        }

        ZipArchiveEntry entry = archive.CreateEntry("upscaler.json");
        await using Stream stream = await entry.OpenAsync(cancellationToken);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(jsonString);
    }
}
