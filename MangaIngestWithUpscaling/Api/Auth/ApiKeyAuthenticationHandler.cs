using System.Security.Claims;
using System.Text.Encodings.Web;
using MangaIngestWithUpscaling.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Api.Auth;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApplicationDbContext _context;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApplicationDbContext context
    )
        : base(options, logger, encoder)
    {
        _context = context;
    }

    /// <summary>
    /// Handles the authentication by extracting the API key from the Authorization header.
    /// Expects a header in the format: "Authorization: ApiKey YOUR_API_KEY".
    /// </summary>
    /// <returns>AuthenticateResult indicating success or failure.</returns>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for the Authorization header
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var authHeaderValue = authHeader.ToString();

        // Validate that the scheme is "ApiKey"
        if (!authHeaderValue.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Invalid authentication scheme.");

        // Extract the API key value after "ApiKey "
        var apiKeyValue = authHeaderValue.Substring("ApiKey ".Length).Trim();
        if (string.IsNullOrEmpty(apiKeyValue))
            return AuthenticateResult.Fail("Invalid API Key");

        var apiKey = await _context
            .ApiKeys.Include(k => k.User)
            .FirstOrDefaultAsync(k => k.Key == apiKeyValue);

        // Use TimeProvider from the base class to get the current UTC time
        var currentUtc = TimeProvider.GetUtcNow().UtcDateTime;

        if (apiKey == null || !apiKey.IsActive)
            return AuthenticateResult.Fail("Invalid API Key");

        if (apiKey.Expiration is not null && apiKey.Expiration < currentUtc)
            return AuthenticateResult.Fail("Expired API Key");

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.UserId),
            new Claim(ClaimTypes.Name, apiKey.User.UserName!),
            new Claim("ApiKey", apiKey.Key),
        };

        var roles = await _context
            .UserRoles.Where(ur => ur.UserId == apiKey.UserId)
            .Select(ur => ur.RoleId)
            .ToListAsync();

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
