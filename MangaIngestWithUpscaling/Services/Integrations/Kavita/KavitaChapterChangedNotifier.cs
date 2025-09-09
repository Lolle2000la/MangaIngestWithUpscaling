using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations.Kavita;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

public class KavitaChapterChangedNotifier(
    IKavitaClient kavitaClient,
    IOptions<KavitaConfiguration> kavitaConfig,
    ILogger<KavitaChapterChangedNotifier> logger) : IChapterChangedNotifier
{
    /// <inheritdoc />
    public async Task Notify(Chapter chapter, bool upscaled)
    {
        if (!kavitaConfig.Value.Enabled) return;

        string? integrationPath = upscaled ?
            chapter.Manga.Library.KavitaConfig.UpscaledMountPoint
            : chapter.Manga.Library.KavitaConfig.NotUpscaledMountPoint;

        if (integrationPath == null) return;

        string folderToScan = Path.GetDirectoryName(Path.Combine(integrationPath, chapter.RelativePath)!)!;

        try
        {
            await kavitaClient.ScanFolder(folderToScan);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to notify Kavita of chapter change for {chapterPath} in mount point {mountPointDir}.",
                chapter.RelativePath, folderToScan);
        }
    }

    /// <inheritdoc />
    public async Task NotifyMangaTitleChanged(Manga manga, string oldTitle, string newTitle)
    {
        if (!kavitaConfig.Value.Enabled) return;

        // If series update is disabled, just scan the new folders
        if (!kavitaConfig.Value.UseSeriesUpdate)
        {
            logger.LogInformation("Series update is disabled. Falling back to folder scanning for manga '{NewTitle}'", newTitle);
            await ScanMangaFolders(manga);
            return;
        }

        try
        {
            // Try to find the existing series by the old title
            var existingSeries = await kavitaClient.FindSeriesByName(oldTitle);
            
            if (existingSeries != null)
            {
                // Update the series metadata with the new title
                await kavitaClient.UpdateSeriesMetadata(existingSeries.Id, newTitle);
                
                // Refresh the series to update file paths after the files have been moved
                await kavitaClient.RefreshSeries(existingSeries.Id);
                
                logger.LogInformation("Successfully updated Kavita series '{OldTitle}' to '{NewTitle}' (ID: {SeriesId})", 
                    oldTitle, newTitle, existingSeries.Id);
            }
            else
            {
                logger.LogWarning("Could not find existing series '{OldTitle}' in Kavita. Will fall back to folder scanning.", oldTitle);
                
                // Fall back to scanning the new directory structure
                await ScanMangaFolders(manga);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to update Kavita series from '{OldTitle}' to '{NewTitle}'. Falling back to folder scanning.", 
                oldTitle, newTitle);
            
            // Fall back to scanning the new directory structure
            await ScanMangaFolders(manga);
        }
    }

    private async Task ScanMangaFolders(Manga manga)
    {
        var library = manga.Library;
        
        // Scan both upscaled and non-upscaled folders if they exist
        var foldersToScan = new List<string>();
        
        if (library.KavitaConfig.NotUpscaledMountPoint != null)
        {
            var notUpscaledFolder = Path.Combine(library.KavitaConfig.NotUpscaledMountPoint, manga.PrimaryTitle);
            foldersToScan.Add(notUpscaledFolder);
        }
        
        if (library.KavitaConfig.UpscaledMountPoint != null)
        {
            var upscaledFolder = Path.Combine(library.KavitaConfig.UpscaledMountPoint, manga.PrimaryTitle);
            foldersToScan.Add(upscaledFolder);
        }

        foreach (var folder in foldersToScan)
        {
            try
            {
                await kavitaClient.ScanFolder(folder);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to scan folder {FolderPath} for manga '{MangaTitle}'", 
                    folder, manga.PrimaryTitle);
            }
        }
    }
}
