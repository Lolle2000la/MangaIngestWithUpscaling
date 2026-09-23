using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Configuration;

/// <summary>
/// Configures <see cref="DbContextOptionsBuilder"/> for the selected <see cref="DatabaseProvider"/>.
/// </summary>
public static class DatabaseSetup
{
    public static void UseDatabaseProvider(
        DbContextOptionsBuilder options,
        DatabaseProvider provider,
        string connectionString,
        string migrationsAssembly
    )
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                options.UseSqlite(
                    connectionString,
                    sqlite =>
                    {
                        sqlite.MigrationsAssembly(migrationsAssembly);
                        sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    }
                );
                break;
            case DatabaseProvider.Postgres:
                options.UseNpgsql(
                    connectionString,
                    npgsql =>
                    {
                        npgsql.MigrationsAssembly(migrationsAssembly);
                        npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                        npgsql.EnableRetryOnFailure();
                    }
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }
}
