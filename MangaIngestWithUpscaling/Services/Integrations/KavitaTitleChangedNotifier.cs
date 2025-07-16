using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.Integrations.Kavita;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations;

/// <summary>
/// Implementation of ITitleChangedNotifier for Kavita integration
/// </summary>
[RegisterScoped]
public class KavitaTitleChangedNotifier(
    IKavitaSeriesUpdateService kavitaSeriesUpdateService,
    IOptions<KavitaConfiguration> kavitaConfig,
    ILogger<KavitaTitleChangedNotifier> logger) : ITitleChangedNotifier
{
    /// <inheritdoc />
    public async Task<bool> NotifyTitleChanged(string oldTitle, string newTitle, Chapter sampleChapter)
    {
        if (!kavitaConfig.Value.Enabled)
        {
            logger.LogDebug("Kavita integration is disabled, skipping title change notification");
            return true; // Return true as this is not an error condition
        }

        try
        {
            logger.LogInformation("Notifying Kavita of title change from '{OldTitle}' to '{NewTitle}'", 
                oldTitle, newTitle);

            // Try to update the existing series with the new title
            var updateSuccess = await kavitaSeriesUpdateService.UpdateSeriesTitle(oldTitle, newTitle, sampleChapter);
            
            if (!updateSuccess)
            {
                logger.LogWarning("Failed to update series title in Kavita, the series may not exist or may need to be rescanned");
                
                // As a fallback, we could trigger a folder scan to let Kavita detect the changes
                // This would still result in a new series being created, but at least the changes would be detected
                return false;
            }

            logger.LogInformation("Successfully notified Kavita of title change from '{OldTitle}' to '{NewTitle}'", 
                oldTitle, newTitle);
            
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error notifying Kavita of title change from '{OldTitle}' to '{NewTitle}'", 
                oldTitle, newTitle);
            return false;
        }
    }
}
