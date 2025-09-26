using System.Security.Cryptography;
using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Services.Auth;

[RegisterScoped]
public class ApiKeyService : IApiKeyService
{
    public string GenerateApiKey()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public async Task<ApiKey> CreateApiKeyAsync(ApplicationDbContext context, string userId)
    {
        var apiKey = new ApiKey
        {
            Key = GenerateApiKey(),
            UserId = userId,
            Expiration = DateTime.UtcNow.AddYears(1),
            IsActive = true,
        };

        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();

        return apiKey;
    }
}
