using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MangaIngestWithUpscaling.Configuration;

/// <summary>
/// The database provider and connection strings resolved from configuration at startup, together
/// with the derived paths and migrations assemblies the rest of the boot sequence needs.
/// </summary>
/// <remarks>
/// <see cref="LoggingConnectionReadOnlyString" /> keeps its historical <c>ReadOnly</c> suffix but is
/// <em>not</em> opened read-only. It is simply the parsed <c>ConnectionStrings:LoggingConnection</c>
/// value, and the boot sequence uses that connection to enable WAL journaling and to vacuum the logs
/// file via the logging context. The application itself only reads log rows through that context
/// (the Serilog sink writes through its own connection, and the property name is retained because
/// callers bind to it).
/// </remarks>
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
        // meaningless). The parsed string is not opened read-only despite its property name: the
        // boot sequence uses this same connection to enable WAL and vacuum the file (see the record
        // remarks); the application only reads log rows through it.
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
