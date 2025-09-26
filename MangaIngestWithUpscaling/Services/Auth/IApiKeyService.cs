using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Services.Auth;

public interface IApiKeyService
{
    string GenerateApiKey();
    Task<ApiKey> CreateApiKeyAsync(ApplicationDbContext context, string userId);
}
