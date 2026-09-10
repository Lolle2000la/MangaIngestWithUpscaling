using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

public class AddMultipleIngestPathsMigrationTests
{
    private const string TargetMigration = "20260910184231_AddMultipleIngestPaths";

    private static string GetPreviousMigration(ApplicationDbContext context)
    {
        // Locate the migration under test by name and use the one before it, so this keeps working
        // when later migrations are added.
        List<string> migrations = context.Database.GetMigrations().ToList();
        int targetIndex = migrations.IndexOf(TargetMigration);
        Assert.True(targetIndex > 0, $"Migration {TargetMigration} not found.");
        return migrations[targetIndex - 1];
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_PreservesExistingIngestPathAsFirstEntry()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new ApplicationDbContext(options);

            IMigrator migrator = context.Database.GetService<IMigrator>();
            string previousMigration = GetPreviousMigration(context);

            await migrator.MigrateAsync(previousMigration, TestContext.Current.CancellationToken);

            // Insert a library using the pre-migration schema, where IngestPath was a single column.
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Libraries\" (\"Name\", \"IngestPath\", \"NotUpscaledLibraryPath\", \"UpscaledLibraryPath\", \"UpscaleOnIngest\", \"MergeChapterParts\") "
                    + "VALUES ('Migrated', '/ingest/old', '/library', NULL, 0, 0);",
                TestContext.Current.CancellationToken
            );

            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

            List<LibraryIngestPath> paths = await context.LibraryIngestPaths.ToListAsync(
                TestContext.Current.CancellationToken
            );

            LibraryIngestPath path = Assert.Single(paths);
            Assert.Equal("/ingest/old", path.Path);
            Assert.Equal(0, path.SortOrder);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Down_RestoresTheFirstConfiguredIngestPath()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new ApplicationDbContext(options);

            IMigrator migrator = context.Database.GetService<IMigrator>();
            string previousMigration = GetPreviousMigration(context);

            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

            // Insert the paths out of SortOrder order so the result proves ordering by SortOrder
            // rather than by insertion order / Id.
            var library = new Library
            {
                Name = "Downgraded",
                IngestPaths =
                [
                    new LibraryIngestPath { Path = "/second", SortOrder = 1 },
                    new LibraryIngestPath { Path = "/first", SortOrder = 0 },
                ],
                NotUpscaledLibraryPath = "/library",
            };
            context.Libraries.Add(library);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await migrator.MigrateAsync(previousMigration, TestContext.Current.CancellationToken);

            List<string> restored = await context
                .Database.SqlQueryRaw<string>(
                    "SELECT \"IngestPath\" AS \"Value\" FROM \"Libraries\""
                )
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.Equal("/first", Assert.Single(restored));
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
