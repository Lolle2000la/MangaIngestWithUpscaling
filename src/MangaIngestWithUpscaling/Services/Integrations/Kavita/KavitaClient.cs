using MangaIngestWithUpscaling.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

public class KavitaClient(
    HttpClient client,
    IOptions<KavitaConfiguration> configuration,
    IStringLocalizer<KavitaClient> localizer
) : IKavitaClient
{
    /// <inheritdoc />
    public async Task ScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.ApiKey))
        {
            throw new InvalidOperationException(localizer["Error_KavitaApiKeyNotConfigured"]);
        }

        await client.PostAsJsonAsync(
            "/api/Library/scan-folder",
            new ScanFolderRequest { ApiKey = configuration.Value.ApiKey, FolderPath = folderPath }
        );
    }
}

public record ScanFolderRequest
{
    public required string ApiKey { get; set; }
    public required string FolderPath { get; set; }
}
