using Microsoft.AspNetCore.Identity;

namespace MangaIngestWithUpscaling.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
    public string? PreferredCulture { get; set; }
}
