using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Data;

public class CustomUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser>
{
    public CustomUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        
        if (!string.IsNullOrEmpty(user.PreferredCulture))
        {
            identity.AddClaim(new Claim("locale", user.PreferredCulture));
        }

        return identity;
    }
}
