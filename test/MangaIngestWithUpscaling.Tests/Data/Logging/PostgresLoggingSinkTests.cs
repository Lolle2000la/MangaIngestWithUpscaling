using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

namespace MangaIngestWithUpscaling.Tests.Data.Logging;

/// <summary>
/// Verifies that the PostgreSQL log sink writes rows that the application's <see cref="LoggingDbContext"/>
/// (used by the logs page) can read. The sink's column-writer configuration lives in
/// <see cref="PostgresLogging"/>; this catches a mismatch between it and the <c>Log</c> entity.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresLoggingSinkTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to run the PostgreSQL logging sink test (Docker required).";

    [Fact]
    public async Task Sink_WritesRowsReadableThroughLoggingDbContext()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase database = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        // The application creates the table itself; mirror that here.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using NpgsqlCommand create = connection.CreateCommand();
            create.CommandText = PostgresLogging.CreateTableSql;
            await create.ExecuteNonQueryAsync(ct);
        }

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.PostgreSQL(
                database.ConnectionString,
                tableName: PostgresLogging.TableName,
                columnOptions: PostgresLogging.ColumnWriters,
                schemaName: PostgresLogging.SchemaName,
                needAutoCreateTable: false,
                batchSizeLimit: 1,
                period: TimeSpan.FromMilliseconds(50)
            )
            .CreateLogger();

        logger.Information("value is {Value}", 42);
        (logger as IDisposable)?.Dispose();

        var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
        DatabaseSetup.UseDatabaseProvider(
            optionsBuilder,
            DatabaseProvider.Postgres,
            database.ConnectionString,
            typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!
        );
        await using var logContext = new LoggingDbContext(optionsBuilder.Options);

        var log = await logContext.LogEntries.SingleAsync(ct);
        Assert.Equal("Information", log.Level);
        Assert.Equal("value is 42", log.RenderedMessage);
        Assert.NotNull(log.Properties);
        Assert.Contains("Value", log.Properties);
        Assert.True(log.Timestamp > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task CreateTableSql_DefinesNonNullableColumnsAndTimestampIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase database = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        // The constant holds multiple statements; this also verifies Npgsql runs them as one batch.
        await using (NpgsqlCommand create = connection.CreateCommand())
        {
            create.CommandText = PostgresLogging.CreateTableSql;
            await create.ExecuteNonQueryAsync(ct);
        }

        var nullability = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (NpgsqlCommand columns = connection.CreateCommand())
        {
            columns.CommandText = """
                SELECT column_name, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Logs'
                """;
            await using NpgsqlDataReader reader = await columns.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                nullability[reader.GetString(0)] = reader.GetString(1) == "YES";
            }
        }

        // These match the non-nullable Log entity properties; the sink always writes them.
        Assert.False(nullability["Level"]);
        Assert.False(nullability["RenderedMessage"]);
        // The entity declares these as nullable, so the DDL must keep them nullable.
        Assert.True(nullability["Exception"]);
        Assert.True(nullability["Properties"]);

        await using NpgsqlCommand index = connection.CreateCommand();
        index.CommandText = """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'Logs'
              AND indexname = 'IX_Logs_Timestamp'
            """;
        string? indexDefinition = (string?)await index.ExecuteScalarAsync(ct);
        Assert.NotNull(indexDefinition);
        // The name alone is not enough: the index must cover the retention DELETE's column.
        Assert.Contains("\"Timestamp\"", indexDefinition);
    }
}
