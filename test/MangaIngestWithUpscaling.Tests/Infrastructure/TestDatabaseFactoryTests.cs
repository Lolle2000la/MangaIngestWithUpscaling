namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// Pins the parsing of <c>TEST_DB_PROVIDER</c>. A permissive fallback to SQLite would let a typo'd
/// value in CI report a passing PostgreSQL job while skipping every PostgreSQL test.
/// </summary>
public class TestDatabaseFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sqlite")]
    [InlineData("SQLite")]
    [InlineData("sqlite3")]
    public void ResolveBackend_EmptyOrSqlite_SelectsSqlite(string? value)
    {
        Assert.Equal(TestDatabaseBackend.Sqlite, TestDatabaseFactory.ResolveBackend(value));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("postgresql")]
    [InlineData(" npgsql ")]
    [InlineData("PostgreSQL")]
    [InlineData("Npgsql")]
    public void ResolveBackend_PostgresAlias_SelectsPostgres(string value)
    {
        Assert.Equal(TestDatabaseBackend.Postgres, TestDatabaseFactory.ResolveBackend(value));
    }

    [Theory]
    [InlineData("postgress")]
    [InlineData("pgsql")]
    [InlineData("sqlitee")]
    public void ResolveBackend_UnrecognizedValue_Throws(string value)
    {
        Assert.Throws<InvalidOperationException>(() => TestDatabaseFactory.ResolveBackend(value));
    }
}
