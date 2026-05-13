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

    public async Task<ApiKey> CreateApiKeyAsync(string userId, ApplicationDbContext dbContext)
    {
        var apiKey = new ApiKey
        {
            Key = GenerateApiKey(),
            UserId = userId,
            Expiration = DateTime.UtcNow.AddYears(1),
            IsActive = true,
        };

        dbContext.ApiKeys.Add(apiKey);
        await dbContext.SaveChangesAsync();

        return apiKey;
    }
}
