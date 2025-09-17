using MangaIngestWithUpscaling.Configuration;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using System.Text;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

public class KavitaClient(
    HttpClient client,
    IOptions<KavitaConfiguration> configuration,
    ILogger<KavitaClient> logger) : IKavitaClient
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
    public async Task<bool> TryUpdateSeries(string oldFolderPath, string newFolderPath, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            logger.LogWarning("Kavita API key is not configured. Cannot update series.");
            return false;
        }

        try
        {
            // First, try to find the series by the old folder path
            var seriesId = await TryFindSeriesByPath(oldFolderPath);
            if (seriesId.HasValue)
            {
                // Attempt to update the series metadata
                var updateSuccess = await TryUpdateSeriesMetadata(seriesId.Value, newTitle, newFolderPath);
                if (updateSuccess)
                {
                    logger.LogInformation("Successfully updated series {SeriesId} from '{OldPath}' to '{NewPath}' with title '{NewTitle}'", 
                        seriesId.Value, oldFolderPath, newFolderPath, newTitle);
                    return true;
                }
            }

            logger.LogDebug("Could not find or update series for path '{OldPath}'. Will fall back to folder scan.", oldFolderPath);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update series from '{OldPath}' to '{NewPath}'. Will fall back to folder scan.", 
                oldFolderPath, newFolderPath);
            return false;
        }
    }

    private async Task<int?> TryFindSeriesByPath(string folderPath)
    {
        try
        {
            // Try common API endpoints that might exist for finding series
            var searchEndpoints = new[]
            {
                $"/api/Series/by-path?apikey={Uri.EscapeDataString(configuration.Value.ApiKey!)}&path={Uri.EscapeDataString(folderPath)}",
                $"/api/Library/series-by-path?apikey={Uri.EscapeDataString(configuration.Value.ApiKey!)}&path={Uri.EscapeDataString(folderPath)}"
            };

            foreach (var endpoint in searchEndpoints)
            {
                try
                {
                    var response = await client.GetAsync(endpoint);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            // Try to parse as a simple series object with an ID
                            using var jsonDoc = JsonDocument.Parse(content);
                            if (jsonDoc.RootElement.TryGetProperty("id", out var idProperty) && 
                                idProperty.TryGetInt32(out var seriesId))
                            {
                                return seriesId;
                            }
                        }
                    }
                }
                catch (HttpRequestException ex) when (ex.Data.Contains("StatusCode") && 
                    ex.Data["StatusCode"]?.ToString() == "404")
                {
                    // Expected if endpoint doesn't exist, continue to next
                    continue;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to find series by path '{FolderPath}'", folderPath);
            return null;
        }
    }

    private async Task<bool> TryUpdateSeriesMetadata(int seriesId, string newTitle, string newFolderPath)
    {
        try
        {
            var updateEndpoints = new[]
            {
                $"/api/Series/{seriesId}",
                $"/api/Series/{seriesId}/metadata"
            };

            var updateData = new
            {
                ApiKey = configuration.Value.ApiKey,
                Title = newTitle,
                Path = newFolderPath,
                Name = newTitle
            };

            foreach (var endpoint in updateEndpoints)
            {
                try
                {
                    var response = await client.PutAsJsonAsync(endpoint, updateData);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    
                    // Also try with PATCH method
                    var patchResponse = await SendPatchAsync(endpoint, updateData);
                    if (patchResponse.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch (HttpRequestException ex) when (ex.Data.Contains("StatusCode") && 
                    (ex.Data["StatusCode"]?.ToString() == "404" || ex.Data["StatusCode"]?.ToString() == "405"))
                {
                    // Expected if endpoint doesn't exist or method not allowed, continue to next
                    continue;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to update series metadata for series {SeriesId}", seriesId);
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendPatchAsync(string endpoint, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Patch, endpoint)
        {
            Content = content
        };
        return await client.SendAsync(request);
    }
}

public record ScanFolderRequest
{
    public required string ApiKey { get; set; }
    public required string FolderPath { get; set; }
}