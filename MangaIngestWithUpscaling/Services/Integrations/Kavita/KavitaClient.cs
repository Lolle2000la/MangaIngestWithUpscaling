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
            // Try multiple potential search endpoints that Kavita might use
            var searchEndpoints = new[]
            {
                $"/api/Series/search?name={Uri.EscapeDataString(seriesName)}&apikey={configuration.Value.ApiKey}",
                $"/api/series/search?name={Uri.EscapeDataString(seriesName)}&apikey={configuration.Value.ApiKey}",
                $"/api/Series?filter={Uri.EscapeDataString(seriesName)}&apikey={configuration.Value.ApiKey}",
                $"/api/series?filter={Uri.EscapeDataString(seriesName)}&apikey={configuration.Value.ApiKey}"
            };
            
            foreach (var endpoint in searchEndpoints)
            {
                try
                {
                    var response = await client.GetAsync(endpoint);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        
                        // Try to parse as array first
                        try
                        {
                            var seriesArray = JsonSerializer.Deserialize<KavitaSeries[]>(content, _jsonOptions);
                            if (seriesArray != null && seriesArray.Length > 0)
                            {
                                // Return the first exact match (case insensitive) or the first result if no exact match
                                return seriesArray.FirstOrDefault(s => string.Equals(s.Name, seriesName, StringComparison.OrdinalIgnoreCase))
                                       ?? seriesArray.FirstOrDefault();
                            }
                        }
                        catch (JsonException)
                        {
                            // Try parsing as a single object
                            try
                            {
                                var singleSeries = JsonSerializer.Deserialize<KavitaSeries>(content, _jsonOptions);
                                if (singleSeries != null && string.Equals(singleSeries.Name, seriesName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return singleSeries;
                                }
                            }
                            catch (JsonException)
                            {
                                // Try parsing as a response wrapper object
                                try
                                {
                                    var wrapper = JsonSerializer.Deserialize<JsonElement>(content, _jsonOptions);
                                    if (wrapper.TryGetProperty("data", out var dataElement) ||
                                        wrapper.TryGetProperty("series", out dataElement) ||
                                        wrapper.TryGetProperty("results", out dataElement))
                                    {
                                        var seriesFromWrapper = JsonSerializer.Deserialize<KavitaSeries[]>(dataElement.GetRawText(), _jsonOptions);
                                        if (seriesFromWrapper != null && seriesFromWrapper.Length > 0)
                                        {
                                            return seriesFromWrapper.FirstOrDefault(s => string.Equals(s.Name, seriesName, StringComparison.OrdinalIgnoreCase))
                                                   ?? seriesFromWrapper.FirstOrDefault();
                                        }
                                    }
                                }
                                catch (JsonException)
                                {
                                    logger.LogWarning("Could not parse series search response from endpoint {Endpoint}", endpoint);
                                }
                            }
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    // Try next endpoint
                    continue;
                }
            }
            
            logger.LogWarning("Could not find series '{SeriesName}' using any available search endpoint", seriesName);
            return null;
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

            // Try multiple potential update endpoints
            var updateEndpoints = new[]
            {
                $"/api/Series/{seriesId}/metadata",
                $"/api/series/{seriesId}/metadata", 
                $"/api/Series/{seriesId}",
                $"/api/series/{seriesId}"
            };
            
            foreach (var endpoint in updateEndpoints)
            {
                try
                {
                    var response = await client.PutAsJsonAsync(endpoint, updateRequest);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogDebug("Successfully updated series metadata using endpoint {Endpoint}", endpoint);
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Try next endpoint
                    continue;
                }
            }
            
            logger.LogWarning("Failed to update series metadata for ID {SeriesId} using any available endpoint", seriesId);
            throw new HttpRequestException($"Failed to update series metadata for ID {seriesId}");
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

            // Try multiple potential refresh endpoints
            var refreshEndpoints = new[]
            {
                $"/api/Series/{seriesId}/refresh",
                $"/api/series/{seriesId}/refresh",
                $"/api/Series/{seriesId}/scan",
                $"/api/series/{seriesId}/scan",
                $"/api/Library/refresh-series?seriesId={seriesId}&apikey={configuration.Value.ApiKey}"
            };
            
            foreach (var endpoint in refreshEndpoints)
            {
                try
                {
                    HttpResponseMessage response;
                    
                    if (endpoint.Contains("Library/refresh-series"))
                    {
                        // This endpoint might use GET instead of POST
                        response = await client.GetAsync(endpoint);
                    }
                    else
                    {
                        response = await client.PostAsJsonAsync(endpoint, refreshRequest);
                    }
                    
                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogDebug("Successfully refreshed series using endpoint {Endpoint}", endpoint);
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Try next endpoint
                    continue;
                }
            }
            
            logger.LogWarning("Failed to refresh series ID {SeriesId} using any available endpoint", seriesId);
            throw new HttpRequestException($"Failed to refresh series ID {seriesId}");
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