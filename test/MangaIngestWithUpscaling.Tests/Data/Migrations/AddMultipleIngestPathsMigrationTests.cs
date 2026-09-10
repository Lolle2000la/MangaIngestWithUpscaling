using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

public class AddMultipleIngestPathsMigrationTests
{
    private const string PreviousMigration = "20260119091931_AddPersistedTaskIndexes";

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
            await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

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
}
