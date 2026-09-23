using System.Data;
using System.Data.Common;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Verifies that the provider's EF migrations apply cleanly to an empty database. This is distinct
/// from the rest of the suite, which builds its schema with <c>EnsureCreated</c> and therefore does
/// not exercise the migration assemblies (especially the PostgreSQL baseline).
/// </summary>
public class DatabaseMigrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_OnFreshDatabase_AppliesAllMigrationsAndCreatesCoreTables()
    {
        await using TestDatabase database = TestDatabaseFactory.Create();
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            TestContext.Current.CancellationToken
        );

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(
            await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)
        );
        Assert.NotEmpty(
            await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
        );

        HashSet<string> tables = await GetTableNamesAsync(context);

        foreach (
            string expected in new[]
            {
                "AspNetUsers",
                "Libraries",
                "LibraryIngestPaths",
                "MangaSeries",
                "Chapters",
                "MergedChapterInfos",
                "PersistedTasks",
                "ApiKeys",
                "DataProtectionKeys",
            }
        )
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_OnFreshDatabase_CreatesTheTaskPayloadIndexes()
    {
        await using TestDatabase database = TestDatabaseFactory.Create();
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            TestContext.Current.CancellationToken
        );

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        HashSet<string> indexes = await GetIndexNamesAsync(context, "PersistedTasks");

        // Both the SQLite functional-index migration and the PostgreSQL baseline create these.
        Assert.Contains("IX_PersistedTasks_Type", indexes);
        Assert.Contains("IX_PersistedTasks_ChapterId", indexes);
        Assert.Contains("IX_PersistedTasks_Type_ChapterId", indexes);
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(ApplicationDbContext context)
    {
        string sql = IsPostgres(context)
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"
            : "SELECT name FROM sqlite_master WHERE type = 'table'";
        return await ReadNamesAsync(context, sql);
    }

    private static async Task<HashSet<string>> GetIndexNamesAsync(
        ApplicationDbContext context,
        string table
    )
    {
        string sql = IsPostgres(context)
            ? "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table"
            : "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @table";
        return await ReadNamesAsync(context, sql, table);
    }

    private static bool IsPostgres(ApplicationDbContext context) =>
        context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
        == true;

    private static async Task<HashSet<string>> ReadNamesAsync(
        ApplicationDbContext context,
        string sql,
        string? table = null
    )
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (table is not null)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken
        );
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
