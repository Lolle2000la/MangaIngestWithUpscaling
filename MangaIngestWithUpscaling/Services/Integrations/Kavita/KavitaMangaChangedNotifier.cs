using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.Integrations.Kavita;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

/// <summary>
/// Notifies Kavita about manga-level changes, attempting to update existing entries before falling back to scanning
/// </summary>
public class KavitaMangaChangedNotifier(
    IKavitaClient kavitaClient,
    IOptions<KavitaConfiguration> kavitaConfig,
    ILogger<KavitaMangaChangedNotifier> logger) : IMangaChangedNotifier
{
    /// <inheritdoc />
    public async Task NotifyTitleChanged(Manga manga, string oldTitle, string oldFolderPath, string newFolderPath)
    {
        if (!kavitaConfig.Value.Enabled) return;

        string? integrationPath = manga.Library.KavitaConfig.NotUpscaledMountPoint;
        if (integrationPath == null) return;

        // Convert local paths to Kavita mount point paths
        var oldKavitaPath = Path.Combine(integrationPath, PathEscaper.EscapeFileName(oldTitle));
        var newKavitaPath = Path.Combine(integrationPath, PathEscaper.EscapeFileName(manga.PrimaryTitle));

        try
        {
            // First, attempt to update the existing series in Kavita
            var updateSuccess = await kavitaClient.TryUpdateSeries(oldKavitaPath, newKavitaPath, manga.PrimaryTitle);
            
            if (updateSuccess)
            {
                logger.LogInformation("Successfully updated series in Kavita for manga '{OldTitle}' -> '{NewTitle}'", 
                    oldTitle, manga.PrimaryTitle);
                
                // Also handle upscaled library if configured
                if (manga.Library.KavitaConfig.UpscaledMountPoint != null)
                {
                    var oldUpscaledKavitaPath = Path.Combine(manga.Library.KavitaConfig.UpscaledMountPoint, PathEscaper.EscapeFileName(oldTitle));
                    var newUpscaledKavitaPath = Path.Combine(manga.Library.KavitaConfig.UpscaledMountPoint, PathEscaper.EscapeFileName(manga.PrimaryTitle));
                    
                    await kavitaClient.TryUpdateSeries(oldUpscaledKavitaPath, newUpscaledKavitaPath, manga.PrimaryTitle);
                }
            }
            else
            {
                // Fall back to scanning both old and new directories
                logger.LogInformation("Could not update series in Kavita, falling back to directory scan for manga '{NewTitle}'", 
                    manga.PrimaryTitle);
                
                await kavitaClient.ScanFolder(Path.GetDirectoryName(newKavitaPath)!);
                
                // Also scan upscaled if configured
                if (manga.Library.KavitaConfig.UpscaledMountPoint != null)
                {
                    var newUpscaledKavitaPath = Path.Combine(manga.Library.KavitaConfig.UpscaledMountPoint, PathEscaper.EscapeFileName(manga.PrimaryTitle));
                    await kavitaClient.ScanFolder(Path.GetDirectoryName(newUpscaledKavitaPath)!);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to notify Kavita of manga title change from '{OldTitle}' to '{NewTitle}'", 
                oldTitle, manga.PrimaryTitle);
        }
    }
}