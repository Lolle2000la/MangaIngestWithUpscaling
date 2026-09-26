using MangaIngestWithUpscaling.Configuration;

namespace MangaIngestWithUpscaling.Tests.Configuration;

public class DatabaseProviderResolverTests
{
    [Theory]
    [InlineData(null, DatabaseProvider.Sqlite)]
    [InlineData("", DatabaseProvider.Sqlite)]
    [InlineData("   ", DatabaseProvider.Sqlite)]
    [InlineData("sqlite", DatabaseProvider.Sqlite)]
    [InlineData("SQLite", DatabaseProvider.Sqlite)]
    [InlineData("sqlite3", DatabaseProvider.Sqlite)]
    [InlineData("postgres", DatabaseProvider.Postgres)]
    [InlineData("PostgreSQL", DatabaseProvider.Postgres)]
    [InlineData("  Postgres  ", DatabaseProvider.Postgres)]
    [InlineData("npgsql", DatabaseProvider.Postgres)]
    public void Resolve_MapsKnownValues(string? value, DatabaseProvider expected) =>
        Assert.Equal(expected, DatabaseProviderResolver.Resolve(value));

    [Theory]
    [InlineData("Postgresqlx")]
    [InlineData("postgress")]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    public void Resolve_UnknownValue_ThrowsInsteadOfDefaultingToSqlite(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseProviderResolver.Resolve(value)
        );
        Assert.Contains(value, exception.Message);
    }
}
