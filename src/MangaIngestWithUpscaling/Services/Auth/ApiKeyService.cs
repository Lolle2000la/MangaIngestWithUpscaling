using System.Security.Cryptography;
using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Auth;

[RegisterScoped]
public class ApiKeyService(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    : IApiKeyService
{
    public string GenerateApiKey()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public async Task<ApiKey> CreateApiKeyAsync(string userId)
    {
        var apiKey = new ApiKey
        {
            Key = GenerateApiKey(),
            UserId = userId,
            Expiration = DateTime.UtcNow.AddYears(1),
            IsActive = true,
        };

        await using var context = await dbContextFactory.CreateDbContextAsync();
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();

        return apiKey;
    }
}
