using MangaIngestWithUpscaling.Data;
using System.Security.Cryptography;

namespace MangaIngestWithUpscaling.Services.Auth;

[RegisterScoped]
public class ApiKeyService : IApiKeyService
{
    private readonly ApplicationDbContext _context;

    public ApiKeyService(ApplicationDbContext context)
    {
        _context = context;
    }

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
            IsActive = true
        };

        _context.ApiKeys.Add(apiKey);
        await _context.SaveChangesAsync();

        return apiKey;
    }
}