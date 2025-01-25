using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Data
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
    {
        public DbSet<Library> Libraries { get; set; }
        public DbSet<LibraryFilterRule> LibraryFilterRules { get; set; }
        public DbSet<Manga> MangaSeries { get; set; }
        public DbSet<MangaAlternativeTitle> MangaAlternativeTitles { get; set; }
        public DbSet<Chapter> Chapters { get; set; }
        public DbSet<UpscalerConfig> UpscalerConfigs { get; set; }

    }
}
