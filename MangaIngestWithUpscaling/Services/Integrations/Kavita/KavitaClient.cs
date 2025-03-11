
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
            new ScanFolderRequest
            {
                ApiKey = configuration.Value.ApiKey,
                FolderPath = folderPath
            });
    }
}

public record ScanFolderRequest
{
    public string ApiKey { get; set; }
    public string FolderPath { get; set; }
}
