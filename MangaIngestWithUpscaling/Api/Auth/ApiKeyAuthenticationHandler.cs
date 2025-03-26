using MangaIngestWithUpscaling.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace MangaIngestWithUpscaling.Api.Auth;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApplicationDbContext _context;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApplicationDbContext context)  // Removed ISystemClock parameter
        : base(options, logger, encoder)  // Updated base constructor
    {
        _context = context;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader))
            return AuthenticateResult.NoResult();

        var apiKeyValue = apiKeyHeader.ToString();
        if (string.IsNullOrEmpty(apiKeyValue))
            return AuthenticateResult.Fail("Invalid API Key");

        var apiKey = await _context.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.Key == apiKeyValue);

        // Use TimeProvider from the base class
        var currentUtc = TimeProvider.GetUtcNow().UtcDateTime;

        if (apiKey == null || !apiKey.IsActive)
            return AuthenticateResult.Fail("Invalid API Key");

        if (apiKey.Expiration.HasValue && apiKey.Expiration < currentUtc)
            return AuthenticateResult.Fail("Expired API Key");


        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.UserId),
            new(ClaimTypes.Name, apiKey.User.UserName!),
            new("ApiKey", apiKey.Key)
        };

        var roles = await _context.UserRoles
            .Where(ur => ur.UserId == apiKey.UserId)
            .Select(ur => ur.RoleId)
            .ToListAsync();

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
