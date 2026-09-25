using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Data.LogModel;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using MangaIngestWithUpscaling.DbMigrator;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// End-to-end tests for <see cref="DataMigrator"/>: real SQLite databases and a throwaway
/// PostgreSQL container, exercising both directions, sequence reset and log copying. These require
/// Docker, so they only run in the PostgreSQL pass (TEST_DB_PROVIDER=postgres).
/// </summary>
[Trait("Category", "Integration")]
public class DbMigratorRoundTripTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to run the DbMigrator round-trip tests (Docker required).";

    [Fact]
    public async Task Migrate_SqliteToPostgresAndBack_PreservesApplicationDataAndResetsSequences()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );
        await using TestDatabase sqliteTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );

        int chapterId;
        await using (ApplicationDbContext seed = sqliteSource.CreateContext())
        {
            var library = new Library
            {
                Name = "Round Trip Library",
                NotUpscaledLibraryPath = "/regular",
                UpscaledLibraryPath = "/upscaled",
                IngestPaths =
                {
                    new LibraryIngestPath { Path = "/ingest", SortOrder = 0 },
                },
            };
            var manga = new Manga { PrimaryTitle = "Round Trip Manga", Library = library };
            var chapter = new Chapter
            {
                FileName = "chapter1.cbz",
                RelativePath = "Round Trip Manga/chapter1.cbz",
                Manga = manga,
            };
            seed.AddRange(library, manga, chapter);
            await seed.SaveChangesAsync(ct);

            chapterId = chapter.Id;
            seed.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = new DetectSplitCandidatesTask(chapterId, detectorVersion: 1),
                    Status = PersistedTaskStatus.Pending,
                    Order = 1,
                }
            );
            await seed.SaveChangesAsync(ct);
        }

        // SQLite -> PostgreSQL
        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Sqlite,
                sqliteSource.ConnectionString,
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        await using (
            ApplicationDbContext postgres = await postgresTarget.CreateContextAsync(
                ensureSchema: false,
                ct
            )
        )
        {
            Assert.Equal(1, await postgres.Libraries.CountAsync(ct));
            Assert.Equal(1, await postgres.LibraryIngestPaths.CountAsync(ct));
            Assert.Equal(1, await postgres.MangaSeries.CountAsync(ct));
            Assert.Equal(1, await postgres.Chapters.CountAsync(ct));

            PersistedTask task = await postgres.PersistedTasks.SingleAsync(ct);
            var data = Assert.IsType<DetectSplitCandidatesTask>(task.Data);
            Assert.Equal(chapterId, data.ChapterId);

            // If sequences were not reset this would collide with the migrated row (Id = 1).
            postgres.Libraries.Add(
                new Library
                {
                    Name = "Added After Migration",
                    NotUpscaledLibraryPath = "/regular2",
                    IngestPaths = { new LibraryIngestPath { Path = "/ingest2" } },
                }
            );
            await postgres.SaveChangesAsync(ct);
            Assert.Equal(2, await postgres.Libraries.CountAsync(ct));
        }

        // PostgreSQL -> SQLite
        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                DatabaseProvider.Sqlite,
                sqliteTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        await using ApplicationDbContext roundTripped = await sqliteTarget.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        Assert.Equal(2, await roundTripped.Libraries.CountAsync(ct));
        // The library added after the first leg (to prove the sequence reset) also has an ingest path.
        Assert.Equal(2, await roundTripped.LibraryIngestPaths.CountAsync(ct));
        PersistedTask roundTrippedTask = await roundTripped
            .PersistedTasks.Where(t => t.Id == 1)
            .SingleAsync(ct);
        var roundTrippedData = Assert.IsType<DetectSplitCandidatesTask>(roundTrippedTask.Data);
        Assert.Equal(chapterId, roundTrippedData.ChapterId);
    }

    [Fact]
    public async Task Migrate_FromPostgresToSqlite_CopiesLogs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase postgresSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );
        await using TestDatabase sqliteTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase sqliteLogsTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );

        // Create the application schema so the migrator's table queries work.
        await using (ApplicationDbContext schema = await postgresSource.CreateContextAsync(ct)) { }

        // Seed a log row in the PostgreSQL source.
        await using (var connection = new NpgsqlConnection(postgresSource.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (NpgsqlCommand create = connection.CreateCommand())
            {
                create.CommandText = PostgresLogging.CreateTableSql;
                await create.ExecuteNonQueryAsync(ct);
            }

            await using NpgsqlCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO \"Logs\" (\"Timestamp\", \"Level\", \"RenderedMessage\", \"Properties\") "
                + "VALUES (now(), 'Information', 'hello from postgres', '{}')";
            await insert.ExecuteNonQueryAsync(ct);
        }

        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Postgres,
                postgresSource.ConnectionString,
                DatabaseProvider.Sqlite,
                sqliteTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: true,
                FromLogsConnection: null,
                ToLogsConnection: sqliteLogsTarget.ConnectionString
            ),
            _ => { },
            ct
        );

        var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
        DatabaseSetup.UseDatabaseProvider(
            optionsBuilder,
            DatabaseProvider.Sqlite,
            sqliteLogsTarget.ConnectionString,
            typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
        );
        await using var logContext = new LoggingDbContext(optionsBuilder.Options);
        Log log = await logContext.LogEntries.SingleAsync(ct);
        Assert.Equal("hello from postgres", log.RenderedMessage);
    }

    [Fact]
    public async Task Migrate_FromSqliteToPostgres_CopiesLogs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase sqliteLogsSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        // Create the application schema so the migrator's table queries work.
        await using (ApplicationDbContext schema = sqliteSource.CreateContext()) { }

        // Seed a log row in the separate SQLite logs database.
        await using (var connection = new SqliteConnection(sqliteLogsSource.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (SqliteCommand create = connection.CreateCommand())
            {
                create.CommandText = SqliteLogsDdl;
                await create.ExecuteNonQueryAsync(ct);
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO \"Logs\" (\"Timestamp\", \"Level\", \"RenderedMessage\", \"Properties\") "
                + "VALUES (datetime('now'), 'Warning', 'hello from sqlite', '{}')";
            await insert.ExecuteNonQueryAsync(ct);
        }

        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Sqlite,
                sqliteSource.ConnectionString,
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: true,
                FromLogsConnection: sqliteLogsSource.ConnectionString,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
        DatabaseSetup.UseDatabaseProvider(
            optionsBuilder,
            DatabaseProvider.Postgres,
            postgresTarget.ConnectionString,
            typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!
        );
        await using var logContext = new LoggingDbContext(optionsBuilder.Options);
        Log log = await logContext.LogEntries.SingleAsync(ct);
        Assert.Equal("hello from sqlite", log.RenderedMessage);
    }

    [Fact]
    public async Task Migrate_FromSqliteToPostgres_CopiedLogsDoNotBlockNewLogWrites()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase sqliteLogsSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        await using (ApplicationDbContext schema = sqliteSource.CreateContext()) { }
        await SeedSqliteLogAsync(sqliteLogsSource.ConnectionString, "hello from sqlite", ct);

        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Sqlite,
                sqliteSource.ConnectionString,
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: true,
                FromLogsConnection: sqliteLogsSource.ConnectionString,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        await using LoggingDbContext logContext = CreateLoggingContext(
            DatabaseProvider.Postgres,
            postgresTarget.ConnectionString
        );
        Log copied = await logContext.LogEntries.AsNoTracking().SingleAsync(ct);
        Assert.Equal("hello from sqlite", copied.RenderedMessage);

        // Regression guard: the application writes logs through a sink that omits "Id". If the
        // identity sequence is not advanced past the copied id, this insert collides and throws.
        logContext.LogEntries.Add(
            new Log
            {
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                RenderedMessage = "written after migration",
                Properties = "{}",
            }
        );
        await logContext.SaveChangesAsync(ct);

        Assert.Equal(2, await logContext.LogEntries.AsNoTracking().CountAsync(ct));
    }

    [Fact]
    public async Task Migrate_FromSqliteToPostgres_WithForce_ClearsExistingLogs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase sqliteLogsSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        await using (ApplicationDbContext schema = sqliteSource.CreateContext()) { }
        await SeedSqliteLogAsync(sqliteLogsSource.ConnectionString, "hello from sqlite", ct);
        await SeedPostgresLogAsync(postgresTarget.ConnectionString, "stale target log", ct);

        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Sqlite,
                sqliteSource.ConnectionString,
                DatabaseProvider.Postgres,
                postgresTarget.ConnectionString,
                BatchSize: 500,
                Force: true,
                IncludeLogs: true,
                FromLogsConnection: sqliteLogsSource.ConnectionString,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );

        await using LoggingDbContext logContext = CreateLoggingContext(
            DatabaseProvider.Postgres,
            postgresTarget.ConnectionString
        );
        Log single = await logContext.LogEntries.AsNoTracking().SingleAsync(ct);
        Assert.Equal("hello from sqlite", single.RenderedMessage);
    }

    [Fact]
    public async Task Migrate_FromSqliteToPostgres_WithoutForce_RefusesANonEmptyLogTable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase sqliteLogsSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        // Seed an application row so this test proves the logs refusal happens before the copy.
        await using (ApplicationDbContext seed = sqliteSource.CreateContext())
        {
            seed.Libraries.Add(
                new Library { Name = "Should Not Be Copied", NotUpscaledLibraryPath = "/regular" }
            );
            await seed.SaveChangesAsync(ct);
        }

        await SeedSqliteLogAsync(sqliteLogsSource.ConnectionString, "hello from sqlite", ct);
        await SeedPostgresLogAsync(postgresTarget.ConnectionString, "stale target log", ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataMigrator.MigrateAsync(
                new MigratorOptions(
                    DatabaseProvider.Sqlite,
                    sqliteSource.ConnectionString,
                    DatabaseProvider.Postgres,
                    postgresTarget.ConnectionString,
                    BatchSize: 500,
                    Force: false,
                    IncludeLogs: true,
                    FromLogsConnection: sqliteLogsSource.ConnectionString,
                    ToLogsConnection: null
                ),
                _ => { },
                ct
            )
        );

        // The refusal must be non-destructive: the stale row is still the only row there.
        await using LoggingDbContext logContext = CreateLoggingContext(
            DatabaseProvider.Postgres,
            postgresTarget.ConnectionString
        );
        Log single = await logContext.LogEntries.AsNoTracking().SingleAsync(ct);
        Assert.Equal("stale target log", single.RenderedMessage);

        // The logs guard runs before the application tables are copied, so the target app tables
        // are still empty even though the source had a row.
        await using ApplicationDbContext appContext = await postgresTarget.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        Assert.Equal(0, await appContext.Libraries.CountAsync(ct));
    }

    [Fact]
    public async Task Migrate_FromPostgresToSqlite_SkipsLogsWhenSourceLogsTableMissing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase postgresSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );
        await using TestDatabase sqliteTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase sqliteLogsTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );

        // EnsureCreated builds the application schema; Logs is not part of it, so this source
        // genuinely has no Logs relation for EF to query.
        await using (ApplicationDbContext seed = await postgresSource.CreateContextAsync(ct))
        {
            seed.Libraries.Add(
                new Library { Name = "No Logs Source", NotUpscaledLibraryPath = "/regular" }
            );
            await seed.SaveChangesAsync(ct);
        }

        await DataMigrator.MigrateAsync(
            new MigratorOptions(
                DatabaseProvider.Postgres,
                postgresSource.ConnectionString,
                DatabaseProvider.Sqlite,
                sqliteTarget.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: true,
                FromLogsConnection: null,
                ToLogsConnection: sqliteLogsTarget.ConnectionString
            ),
            _ => { },
            ct
        );

        await using ApplicationDbContext target = await sqliteTarget.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        Assert.Equal(1, await target.Libraries.CountAsync(ct));

        // The missing source table must be skipped, not created on the target.
        await using var logsConnection = new SqliteConnection(sqliteLogsTarget.ConnectionString);
        await logsConnection.OpenAsync(ct);
        await using SqliteCommand check = logsConnection.CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Logs';";
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync(ct))!);
    }

    private static LoggingDbContext CreateLoggingContext(
        DatabaseProvider provider,
        string connectionString
    )
    {
        var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
        DatabaseSetup.UseDatabaseProvider(
            optionsBuilder,
            provider,
            connectionString,
            provider == DatabaseProvider.Sqlite
                ? typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
                : typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!
        );
        return new LoggingDbContext(optionsBuilder.Options);
    }

    private static async Task SeedSqliteLogAsync(
        string connectionString,
        string message,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = SqliteLogsDdl;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO \"Logs\" (\"Timestamp\", \"Level\", \"RenderedMessage\", \"Properties\") "
            + "VALUES (datetime('now'), 'Warning', @message, '{}')";
        insert.Parameters.AddWithValue("@message", message);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task SeedPostgresLogAsync(
        string connectionString,
        string message,
        CancellationToken ct
    )
    {
        await using LoggingDbContext logContext = CreateLoggingContext(
            DatabaseProvider.Postgres,
            connectionString
        );
        await logContext.Database.ExecuteSqlRawAsync(PostgresLogging.CreateTableSql, ct);
        logContext.LogEntries.Add(
            new Log
            {
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                RenderedMessage = message,
                Properties = "{}",
            }
        );
        await logContext.SaveChangesAsync(ct);
    }

    private const string SqliteLogsDdl = """
        CREATE TABLE IF NOT EXISTS "Logs" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Timestamp" TEXT NOT NULL,
            "Level" TEXT,
            "Exception" TEXT,
            "RenderedMessage" TEXT,
            "Properties" TEXT
        );
        """;
}
