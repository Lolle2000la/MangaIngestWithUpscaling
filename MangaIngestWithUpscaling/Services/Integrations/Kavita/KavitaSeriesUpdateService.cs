using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

/// <summary>
/// Service for managing series updates in Kavita when manga titles are changed
/// </summary>
public interface IKavitaSeriesUpdateService
{
    /// <summary>
    /// Updates an existing series in Kavita when a manga title is changed
    /// </summary>
    /// <param name="oldTitle">The original title of the manga</param>
    /// <param name="newTitle">The new title of the manga</param>
    /// <param name="chapter">A representative chapter from the manga to determine library context</param>
    /// <returns>True if the series was found and updated successfully, false otherwise</returns>
    Task<bool> UpdateSeriesTitle(string oldTitle, string newTitle, Chapter chapter);
    
    /// <summary>
    /// Forces a refresh of series metadata after title changes
    /// </summary>
    /// <param name="seriesTitle">The title of the series to refresh</param>
    /// <param name="chapter">A representative chapter from the manga to determine library context</param>
    /// <returns>True if the refresh was initiated successfully</returns>
    Task<bool> RefreshSeriesAfterTitleChange(string seriesTitle, Chapter chapter);
}

[RegisterScoped]
public class KavitaSeriesUpdateService(
    IKavitaClient kavitaClient,
    IOptions<KavitaConfiguration> kavitaConfig,
    ILogger<KavitaSeriesUpdateService> logger) : IKavitaSeriesUpdateService
{
    /// <inheritdoc />
    public async Task<bool> UpdateSeriesTitle(string oldTitle, string newTitle, Chapter chapter)
    {
        if (!kavitaConfig.Value.Enabled)
        {
            logger.LogDebug("Kavita integration is disabled, skipping series title update");
            return true; // Return true as this is not an error condition
        }

        try
        {
            logger.LogInformation("Attempting to update series title in Kavita from '{OldTitle}' to '{NewTitle}'", 
                oldTitle, newTitle);

            // First, try to find the series by the old title
            var seriesInfo = await kavitaClient.FindSeriesByName(oldTitle);
            
            if (seriesInfo == null)
            {
                logger.LogWarning("Could not find series with title '{OldTitle}' in Kavita", oldTitle);
                return false;
            }

            logger.LogInformation("Found series '{SeriesName}' with ID {SeriesId} in Kavita", 
                seriesInfo.Name, seriesInfo.Id);

            // Update the series metadata with the new title
            var updateSuccess = await kavitaClient.UpdateSeriesMetadata(seriesInfo.Id, newTitle);
            
            if (!updateSuccess)
            {
                logger.LogError("Failed to update series metadata for series ID {SeriesId}", seriesInfo.Id);
                return false;
            }

            logger.LogInformation("Successfully updated series ID {SeriesId} title to '{NewTitle}'", 
                seriesInfo.Id, newTitle);

            // Force a refresh of the series metadata to ensure the file paths are updated
            var refreshSuccess = await kavitaClient.RefreshSeriesMetadata(seriesInfo.Id);
            
            if (!refreshSuccess)
            {
                logger.LogWarning("Successfully updated series title but failed to refresh metadata for series ID {SeriesId}", 
                    seriesInfo.Id);
                // We don't return false here as the main update succeeded
            }
            else
            {
                logger.LogInformation("Successfully refreshed metadata for series ID {SeriesId}", seriesInfo.Id);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating series title in Kavita from '{OldTitle}' to '{NewTitle}'", 
                oldTitle, newTitle);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshSeriesAfterTitleChange(string seriesTitle, Chapter chapter)
    {
        if (!kavitaConfig.Value.Enabled)
        {
            logger.LogDebug("Kavita integration is disabled, skipping series refresh");
            return true; // Return true as this is not an error condition
        }

        try
        {
            logger.LogInformation("Attempting to refresh series '{SeriesTitle}' in Kavita", seriesTitle);

            // Find the series by the new title
            var seriesInfo = await kavitaClient.FindSeriesByName(seriesTitle);
            
            if (seriesInfo == null)
            {
                logger.LogWarning("Could not find series with title '{SeriesTitle}' in Kavita for refresh", seriesTitle);
                return false;
            }

            // Force a refresh of the series metadata
            var refreshSuccess = await kavitaClient.RefreshSeriesMetadata(seriesInfo.Id);
            
            if (!refreshSuccess)
            {
                logger.LogError("Failed to refresh metadata for series ID {SeriesId}", seriesInfo.Id);
                return false;
            }

            logger.LogInformation("Successfully refreshed metadata for series '{SeriesTitle}' (ID: {SeriesId})", 
                seriesTitle, seriesInfo.Id);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refreshing series '{SeriesTitle}' in Kavita", seriesTitle);
            return false;
        }
    }
}
