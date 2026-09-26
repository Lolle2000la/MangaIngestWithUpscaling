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

        Dictionary<string, string> definitions = await GetIndexDefinitionsAsync(
            context,
            "PersistedTasks"
        );

        // Both the SQLite functional-index migration and the PostgreSQL baseline create these, and
        // their expressions must reference the JSON paths the raw queries use. Asserting the
        // definition (not just the name) catches a change to the raw index SQL.
        Assert.Contains("IX_PersistedTasks_Type", definitions.Keys);
        Assert.Contains("IX_PersistedTasks_ChapterId", definitions.Keys);
        Assert.Contains("IX_PersistedTasks_Type_ChapterId", definitions.Keys);

        string typePath = IsPostgres(context) ? "$type" : "$.$type";
        string chapterPath = IsPostgres(context) ? "ChapterId" : "$.ChapterId";
        Assert.Contains(typePath, definitions["IX_PersistedTasks_Type"]);
        Assert.Contains(chapterPath, definitions["IX_PersistedTasks_ChapterId"]);
        Assert.Contains(typePath, definitions["IX_PersistedTasks_Type_ChapterId"]);
        Assert.Contains(chapterPath, definitions["IX_PersistedTasks_Type_ChapterId"]);
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(ApplicationDbContext context)
    {
        string sql = IsPostgres(context)
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"
            : "SELECT name FROM sqlite_master WHERE type = 'table'";
        return await ReadNamesAsync(context, sql);
    }

    private static async Task<Dictionary<string, string>> GetIndexDefinitionsAsync(
        ApplicationDbContext context,
        string table
    )
    {
        string sql = IsPostgres(context)
            ? "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table"
            : "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = @table";
        return await ReadNameValuePairsAsync(context, sql, table);
    }

    private static async Task<Dictionary<string, string>> ReadNameValuePairsAsync(
        ApplicationDbContext context,
        string sql,
        string table
    )
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken
        );
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        }

        return result;
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
