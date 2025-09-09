using MangaIngestWithUpscaling.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

public class KavitaClient(
    HttpClient client,
    IOptions<KavitaConfiguration> configuration,
    ILogger<KavitaClient> logger) : IKavitaClient
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
    public async Task<KavitaSeries?> FindSeriesByName(string seriesName)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        try
        {
            // Use search endpoint to find series by name
            var response = await client.GetAsync($"/api/Series/search?name={Uri.EscapeDataString(seriesName)}&apikey={configuration.Value.ApiKey}");
            
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to search for series '{SeriesName}': {StatusCode}", seriesName, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var series = JsonSerializer.Deserialize<KavitaSeries[]>(content, _jsonOptions);
            
            // Return the first exact match or the first result if no exact match
            return series?.FirstOrDefault(s => string.Equals(s.Name, seriesName, StringComparison.OrdinalIgnoreCase)) 
                   ?? series?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching for series '{SeriesName}'", seriesName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task UpdateSeriesMetadata(int seriesId, string newName)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        try
        {
            var updateRequest = new UpdateSeriesRequest
            {
                ApiKey = configuration.Value.ApiKey,
                Name = newName
            };

            var response = await client.PutAsJsonAsync($"/api/Series/{seriesId}/metadata", updateRequest);
            
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to update series metadata for ID {SeriesId}: {StatusCode}", seriesId, response.StatusCode);
                throw new HttpRequestException($"Failed to update series metadata: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating series metadata for ID {SeriesId}", seriesId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RefreshSeries(int seriesId)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException("Kavita API key is not configured.");
        }

        try
        {
            var refreshRequest = new RefreshSeriesRequest
            {
                ApiKey = configuration.Value.ApiKey
            };

            var response = await client.PostAsJsonAsync($"/api/Series/{seriesId}/refresh", refreshRequest);
            
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to refresh series ID {SeriesId}: {StatusCode}", seriesId, response.StatusCode);
                throw new HttpRequestException($"Failed to refresh series: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refreshing series ID {SeriesId}", seriesId);
            throw;
        }
    }
}

public record ScanFolderRequest
{
    public required string ApiKey { get; set; }
    public required string FolderPath { get; set; }
}