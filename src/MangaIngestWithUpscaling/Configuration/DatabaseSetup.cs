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
                // Note: retry-on-failure is intentionally not enabled. Several services open
                // user-initiated transactions, which a retrying execution strategy rejects unless
                // every transaction is wrapped in CreateExecutionStrategy().
                options.UseNpgsql(
                    connectionString,
                    npgsql =>
                    {
                        npgsql.MigrationsAssembly(migrationsAssembly);
                        npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    }
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }
}
