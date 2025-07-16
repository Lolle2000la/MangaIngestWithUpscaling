using System.Text.Json;
using MangaIngestWithUpscaling.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

public class KavitaClient(
    HttpClient client,
    IOptions<KavitaConfiguration> configuration) : IKavitaClient
{
    /// <inheritdoc />
    public async Task ScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        await client.PostAsJsonAsync("/api/Library/scan-folder",
            new ScanFolderRequest { ApiKey = configuration.Value.ApiKey, FolderPath = folderPath });
    }

    /// <inheritdoc />
    public async Task<KavitaSeriesInfo?> FindSeriesByName(string seriesName)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        try
        {
            // Get all series and search by name
            // Note: This is a simplified implementation. In a real scenario, we might want to use
            // Kavita's search API or filter by library if available
            var response = await client.GetAsync($"/api/Series/all-v2?apikey={configuration.Value.ApiKey}");
            
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var seriesResponse = await response.Content.ReadFromJsonAsync<KavitaSeriesResponse[]>();
            
            if (seriesResponse == null)
            {
                return null;
            }

            // Look for series with matching name (case-insensitive)
            var matchingSeries = seriesResponse.FirstOrDefault(s => 
                string.Equals(s.Name, seriesName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.LocalizedName, seriesName, StringComparison.OrdinalIgnoreCase));

            if (matchingSeries == null)
            {
                return null;
            }

            return new KavitaSeriesInfo
            {
                Id = matchingSeries.Id,
                Name = matchingSeries.Name,
                LocalizedName = matchingSeries.LocalizedName,
                SortName = matchingSeries.SortName,
                LibraryId = matchingSeries.LibraryId,
                LibraryName = matchingSeries.Library.Name
            };
        }
        catch (Exception)
        {
            // Log error if needed, but return null for now
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSeriesMetadata(int seriesId, string newName, string? newSortName = null)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        try
        {
            // First, get the current series data
            var seriesResponse = await client.GetAsync($"/api/Series/{seriesId}?apikey={configuration.Value.ApiKey}");
            
            if (!seriesResponse.IsSuccessStatusCode)
            {
                return false;
            }

            var currentSeries = await seriesResponse.Content.ReadFromJsonAsync<KavitaSeriesResponse>();
            
            if (currentSeries == null)
            {
                return false;
            }

            // Create update request with new name but preserving other metadata
            var updateRequest = new UpdateSeriesMetadataRequest
            {
                ApiKey = configuration.Value.ApiKey,
                Id = seriesId,
                Name = newName,
                LocalizedName = newName, // Update both name and localized name
                SortName = newSortName ?? newName, // Use provided sort name or default to new name
                SortNameLocked = false, // Allow sort name to be updated
                LocalizedNameLocked = false, // Allow localized name to be updated  
                CoverImageLocked = false // Don't lock cover image
            };

            var response = await client.PostAsJsonAsync("/api/Series/update", updateRequest);
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshSeriesMetadata(int seriesId)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        try
        {
            // First, get the series to determine its library ID
            var seriesResponse = await client.GetAsync($"/api/Series/{seriesId}?apikey={configuration.Value.ApiKey}");
            
            if (!seriesResponse.IsSuccessStatusCode)
            {
                return false;
            }

            var series = await seriesResponse.Content.ReadFromJsonAsync<KavitaSeriesResponse>();
            
            if (series == null)
            {
                return false;
            }

            // Create refresh request
            var refreshRequest = new RefreshSeriesMetadataRequest
            {
                ApiKey = configuration.Value.ApiKey,
                LibraryId = series.LibraryId,
                SeriesId = seriesId,
                ForceUpdate = true, // Force update to ensure changes are applied
                ForceColorscape = true // Also refresh cover/colorscape
            };

            var response = await client.PostAsJsonAsync("/api/Series/refresh-metadata", refreshRequest);
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public record ScanFolderRequest
{
    public required string ApiKey { get; set; }
    public required string FolderPath { get; set; }
}