using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MangaIngestWithUpscaling.Configuration;

/// <summary>
/// The database provider and connection strings resolved from configuration at startup, together
/// with the derived paths and migrations assemblies the rest of the boot sequence needs.
/// </summary>
public sealed record DatabaseConfiguration(
    DatabaseProvider Provider,
    bool IsSqlite,
    string ApplicationConnectionString,
    string ApplicationMigrationsAssembly,
    string PostgresConnectionString,
    string? SqliteDatabasePath,
    string? LoggingConnectionReadOnlyString,
    string LogsDbPath
)
{
    /// <summary>
    /// Resolves the provider and its connection strings. Each provider's connection string is only
    /// required when that provider is selected, so a PostgreSQL deployment does not need
    /// <c>DefaultConnection</c> and vice versa (the shipped appsettings.json provides both
    /// defaults).
    /// </summary>
    public static DatabaseConfiguration Resolve(IConfiguration configuration)
    {
        DatabaseProvider provider = DatabaseProviderResolver.Resolve(
            configuration.GetValue<string>("DatabaseProvider")
        );
        bool isSqlite = provider == DatabaseProvider.Sqlite;

        string sqliteConnectionString = isSqlite
            ? configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "Connection string 'DefaultConnection' not found while DatabaseProvider is 'Sqlite'."
                )
            : string.Empty;

        string postgresConnectionString = isSqlite
            ? string.Empty
            : configuration.GetConnectionString("PostgresConnection")
                ?? throw new InvalidOperationException(
                    "Connection string 'PostgresConnection' not found while DatabaseProvider is 'Postgres'."
                );

        string applicationConnectionString = isSqlite
            ? sqliteConnectionString
            : postgresConnectionString;
        string applicationMigrationsAssembly = isSqlite
            ? typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
            : typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!;

        string? sqliteDatabasePath = null;
        if (isSqlite)
        {
            sqliteDatabasePath = Path.GetFullPath(
                new SqliteConnectionStringBuilder(sqliteConnectionString).DataSource
            );
        }

        // The logs database is only a local SQLite file; on PostgreSQL the logs live in the
        // application database, so this connection string is neither read nor parsed for PostgreSQL
        // (a deployment may repurpose LoggingConnection, and parsing it as SQLite would be
        // meaningless).
        string? loggingConnectionReadOnlyString = null;
        var logsDbPath = string.Empty;
        if (isSqlite)
        {
            var loggingConnectionString =
                configuration.GetConnectionString("LoggingConnection") ?? "Data Source=logs.db";
            var loggingConnectionReadOnlyStringBuilder = new SqliteConnectionStringBuilder(
                loggingConnectionString
            );
            loggingConnectionReadOnlyString =
                loggingConnectionReadOnlyStringBuilder.ConnectionString;
            logsDbPath = Path.GetFullPath(loggingConnectionReadOnlyStringBuilder.DataSource);
        }

        return new DatabaseConfiguration(
            provider,
            isSqlite,
            applicationConnectionString,
            applicationMigrationsAssembly,
            postgresConnectionString,
            sqliteDatabasePath,
            loggingConnectionReadOnlyString,
            logsDbPath
        );
    }
}
