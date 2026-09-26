using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MangaIngestWithUpscaling.Tests.Configuration;

public class DatabaseConfigurationTests
{
    [Fact]
    public void Resolve_Sqlite_ReadsConnectionStringsAndDerivesPaths()
    {
        var configuration = BuildConfiguration(
            ("ConnectionStrings:DefaultConnection", "Data Source=main.db"),
            ("ConnectionStrings:LoggingConnection", "Data Source=logs.db")
        );

        DatabaseConfiguration resolved = DatabaseConfiguration.Resolve(configuration);

        Assert.Equal(DatabaseProvider.Sqlite, resolved.Provider);
        Assert.True(resolved.IsSqlite);
        Assert.Equal("Data Source=main.db", resolved.ApplicationConnectionString);
        Assert.Equal(
            typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName,
            resolved.ApplicationMigrationsAssembly
        );
        Assert.Equal(Path.GetFullPath("main.db"), resolved.SqliteDatabasePath);
        Assert.Equal(Path.GetFullPath("logs.db"), resolved.LogsDbPath);
        Assert.NotNull(resolved.LoggingConnectionReadOnlyString);
        // Not read for SQLite; a deployment may repurpose it for the logs connection.
        Assert.Equal("", resolved.PostgresConnectionString);
    }

    [Fact]
    public void Resolve_SqliteWithoutLoggingConnection_DefaultsToLogsDb()
    {
        var configuration = BuildConfiguration(
            ("ConnectionStrings:DefaultConnection", "Data Source=main.db")
        );

        DatabaseConfiguration resolved = DatabaseConfiguration.Resolve(configuration);

        Assert.Equal(Path.GetFullPath("logs.db"), resolved.LogsDbPath);
    }

    [Fact]
    public void Resolve_Postgres_IgnoresSqliteOnlySettings()
    {
        var configuration = BuildConfiguration(
            ("DatabaseProvider", "Postgres"),
            ("ConnectionStrings:PostgresConnection", "Host=db;Database=manga")
        );

        DatabaseConfiguration resolved = DatabaseConfiguration.Resolve(configuration);

        Assert.Equal(DatabaseProvider.Postgres, resolved.Provider);
        Assert.False(resolved.IsSqlite);
        Assert.Equal("Host=db;Database=manga", resolved.ApplicationConnectionString);
        Assert.Equal("Host=db;Database=manga", resolved.PostgresConnectionString);
        Assert.Equal(
            typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName,
            resolved.ApplicationMigrationsAssembly
        );
        // Neither SQLite path is derived when PostgreSQL is selected.
        Assert.Null(resolved.SqliteDatabasePath);
        Assert.Null(resolved.LoggingConnectionReadOnlyString);
        Assert.Equal("", resolved.LogsDbPath);
    }

    [Fact]
    public void Resolve_SqliteWithoutDefaultConnection_Throws()
    {
        var configuration = BuildConfiguration();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConfiguration.Resolve(configuration)
        );

        Assert.Contains("DefaultConnection", exception.Message);
    }

    [Fact]
    public void Resolve_PostgresWithoutConnectionString_Throws()
    {
        var configuration = BuildConfiguration(("DatabaseProvider", "Postgres"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConfiguration.Resolve(configuration)
        );

        Assert.Contains("PostgresConnection", exception.Message);
    }

    private static IConfiguration BuildConfiguration(
        params (string Key, string Value)[] settings
    ) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
}
