using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MangaIngestWithUpscaling.Data.Sqlite;

/// <summary>
/// Design-time factory used by the EF CLI to scaffold SQLite migrations without booting the web
/// application.
/// </summary>
public sealed class DesignTimeApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=design-time.db",
            sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName)
        );

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
