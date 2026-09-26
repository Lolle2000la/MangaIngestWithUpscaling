using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using LogEntity = MangaIngestWithUpscaling.Data.LogModel.Log;

namespace MangaIngestWithUpscaling.Tests.Data.Logging;

/// <summary>
/// Pins the contract between the external Serilog SQLite sink's <c>Logs</c> table and the
/// application's <see cref="LoggingDbContext"/>. The sink (not the application) creates that table,
/// so this guards both its column shape and the UTC storage the logs UI relies on for correct
/// local-time display.
/// </summary>
public class SqliteLoggingSinkTests
{
    [Fact]
    public async Task Sink_WritesUtcTimestampAndSinkColumnsMatchLogEntity()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"manga-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string dbPath = Path.Combine(directory, "logs.db");

            // Mirrors the application's SQLite Serilog configuration (Program.cs).
            var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.SQLite(
                    dbPath,
                    tableName: "Logs",
                    storeTimestampInUtc: true,
                    retentionPeriod: TimeSpan.FromDays(7),
                    maxDatabaseSize: 100,
                    rollOver: false
                )
                .CreateLogger();

            logger.Information("value is {Value}", 42);
            // Disposing drains and flushes the sink's background batch before the row is read back.
            (logger as IDisposable)?.Dispose();

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
            }.ToString();
            var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
            DatabaseSetup.UseDatabaseProvider(
                optionsBuilder,
                DatabaseProvider.Sqlite,
                connectionString,
                typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
            );
            await using var logContext = new LoggingDbContext(optionsBuilder.Options);

            var log = await logContext.LogEntries.SingleAsync(ct);
            Assert.Equal("Information", log.Level);
            Assert.Equal("value is 42", log.RenderedMessage);
            Assert.NotNull(log.Properties);
            Assert.Contains("Value", log.Properties);

            // The sink stores the UTC wall-clock as TEXT; EF reads it back as Unspecified. With the
            // sink left at storeTimestampInUtc: false on a non-UTC host the value would be shifted
            // by the host's UTC offset and this assertion would fail.
            DateTime storedUtc = DateTime.SpecifyKind(log.Timestamp, DateTimeKind.Utc);
            Assert.True(
                (storedUtc - DateTime.UtcNow).Duration() < TimeSpan.FromMinutes(5),
                $"Expected the stored log timestamp to be UTC, but got {log.Timestamp:o}."
            );

            await AssertSinkColumnsMatchModelAsync(connectionString, logContext, ct);
        }
        finally
        {
            // Release the sink's pooled SQLite connections so the temp directory can be removed.
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Sink_PersistsSerilogLevelNames()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"manga-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string dbPath = Path.Combine(directory, "logs.db");

            var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.SQLite(
                    dbPath,
                    tableName: "Logs",
                    storeTimestampInUtc: true,
                    retentionPeriod: TimeSpan.FromDays(7),
                    maxDatabaseSize: 100,
                    rollOver: false
                )
                .CreateLogger();

            logger.Verbose("verbose");
            logger.Debug("debug");
            logger.Information("information");
            logger.Warning("warning");
            logger.Error("error");
            logger.Fatal("fatal");
            (logger as IDisposable)?.Dispose();

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
            }.ToString();
            var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
            DatabaseSetup.UseDatabaseProvider(
                optionsBuilder,
                DatabaseProvider.Sqlite,
                connectionString,
                typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
            );
            await using var logContext = new LoggingDbContext(optionsBuilder.Options);

            List<string> stored = await logContext
                .LogEntries.Select(log => log.Level)
                .ToListAsync(ct);

            // The logs page filters on these exact strings (LogLevelFilter), so this pins the sink
            // side of the contract: a Fatal event must be stored as "Fatal", not "Critical".
            Assert.Equal(
                Enum.GetNames<LogEventLevel>().OrderBy(name => name),
                stored.OrderBy(name => name)
            );
        }
        finally
        {
            // Release the sink's pooled SQLite connections so the temp directory can be removed.
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static async Task AssertSinkColumnsMatchModelAsync(
        string connectionString,
        LoggingDbContext logContext,
        CancellationToken ct
    )
    {
        // Case-insensitive because the sink declares the primary key as lowercase "id" whereas the
        // model maps it to "Id". Nullability is deliberately not compared: the sink declares every
        // non-key column nullable while the model refines some as required.
        string[] modelColumns = logContext
            .Model.FindEntityType(typeof(LogEntity))!
            .GetProperties()
            .Select(p => p.GetColumnName().ToLowerInvariant())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        var sinkColumns = new List<string>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(\"Logs\")";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // cid, name, type, notnull, dflt_value, pk
                sinkColumns.Add(reader.GetString(1).ToLowerInvariant());
            }
        }

        Assert.Equal(modelColumns, sinkColumns.OrderBy(c => c, StringComparer.Ordinal));
    }
}
