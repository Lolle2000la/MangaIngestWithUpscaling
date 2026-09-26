using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Guards the migrations: the runtime model must match the last snapshot of the active provider, and
/// the historical SQLite migration ids must stay present so that existing installations are not
/// re-migrated.
/// </summary>
public class MigrationModelSnapshotTests
{
    // The migration ids that shipped before the data layer was extracted. Existing SQLite databases
    // record these in __EFMigrationsHistory; renaming or removing one would break upgrades.
    private static readonly string[] HistoricalSqliteMigrationIds =
    {
        "20250127214307_Init",
        "20250127215900_AddShouldUpscaleColumn",
        "20250127223935_ImproveLibraryColumns",
        "20250128000643_ImproveLibraryColumnsFurther",
        "20250129192101_MakeAuthorNullable",
        "20250129214544_MakeRestrictionsBetter",
        "20250202195715_RenameUpscalerConfigToProfile",
        "20250202202027_RemoveRedundantUpscaleQueue",
        "20250202214228_MakeUpscalerConfigSoftDelete",
        "20250203173558_AddOrderToTasksWithSequence",
        "20250205124501_MakeAlternativeTitlesKeyNatural",
        "20250207225924_AddUpscaleOnIngestOption",
        "20250210151544_AddDataProtectionKeys",
        "20250311184623_KavitaMountPointConfig",
        "20250615145526_AddLibraryRenameRules",
        "20250618214731_ReconcileMigrations",
        "20250703200202_AddUpscalerProfilePreference",
        "20250724204233_AddChapterMerging",
        "20250822054134_AddAutoDeleteOddOneOutImagesOption",
        "20250824140436_AddFilteredImages",
        "20250824153205_AddPerceptualHashToFilteredImage",
        "20250824154536_ChangePerceptualHashToUlong",
        "20250824155006_AddPerceptualHashIndex",
        "20250831163134_RemoveAutoDeleteOddOneOutImages",
        "20250923222112_AddIndexForTaskTypesAndChapterId",
        "20250926212307_AddEntityTimestamps",
        "20251207212743_ExpandIndexes",
        "20251220223242_AddIndexForPersistedTask",
        "20251224010601_AddStripDetection",
        "20251227154928_AddPreferredCulture",
        "20260119091931_AddPersistedTaskIndexes",
        "20260910184231_AddMultipleIngestPaths",
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RuntimeModel_MatchesTheActiveProviderSnapshot()
    {
        await using TestDatabase database = TestDatabaseFactory.Create();
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            TestContext.Current.CancellationToken
        );

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF model differs from the last migration snapshot of the active provider. "
                + "Run scripts/create-dual-migration.sh <Name> and commit both migrations."
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExistingSqliteMigrationHistory_IsStillRecognizedAndIdempotent()
    {
        await using TestDatabase database = TestDatabaseFactory.Create(TestDatabaseBackend.Sqlite);
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            TestContext.Current.CancellationToken
        );

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(
            await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)
        );

        List<string> applied = (
            await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
        ).ToList();
        Assert.All(HistoricalSqliteMigrationIds, id => Assert.Contains(id, applied));

        // Re-applying must be a no-op: no new migrations, same history.
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(
            await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            applied.Count,
            (
                await context.Database.GetAppliedMigrationsAsync(
                    TestContext.Current.CancellationToken
                )
            ).Count()
        );
    }
}
