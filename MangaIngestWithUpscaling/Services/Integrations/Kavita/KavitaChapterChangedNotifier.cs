using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data.LibraryManagement;
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
}
