using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Data.LogModel;
using MangaIngestWithUpscaling.Data.Sqlite;
using MangaIngestWithUpscaling.DbMigrator;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Tests.Infrastructure;
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
}
