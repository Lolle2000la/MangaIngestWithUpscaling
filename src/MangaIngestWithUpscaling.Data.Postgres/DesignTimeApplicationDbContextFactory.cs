using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MangaIngestWithUpscaling.Data.Postgres;

/// <summary>
/// Design-time factory used by the EF CLI to scaffold PostgreSQL migrations without booting the web
/// application.
/// </summary>
public sealed class DesignTimeApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=mangaingest_design;Username=postgres;Password=postgres",
            npgsql =>
                npgsql.MigrationsAssembly(
                    typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName
                )
        );

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
