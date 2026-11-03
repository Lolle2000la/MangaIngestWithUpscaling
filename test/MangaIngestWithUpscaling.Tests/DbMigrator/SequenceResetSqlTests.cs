using MangaIngestWithUpscaling.DbMigrator;

namespace MangaIngestWithUpscaling.Tests.DbMigrator;

/// <summary>
/// Pins the SQL the migrator uses to advance a PostgreSQL identity sequence after copying rows with
/// explicit ids. <c>setval</c> is volatile, so the statement must call it exactly once with scalar
/// arguments. The previous form (<c>SELECT setval(...) ... FROM "table"</c>) evaluated it once per
/// source row and never at all on an empty table, so its <c>COALESCE(..., 1)</c> empty-table
/// handling was dead code.
/// </summary>
public class SequenceResetSqlTests
{
    [Fact]
    public void BuildSequenceResetSql_EmitsTheExactStatement()
    {
        string sql = DataMigrator.BuildSequenceResetSql("Libraries");

        Assert.Equal(
            """SELECT setval(pg_get_serial_sequence('"Libraries"', 'Id'), (SELECT COALESCE(MAX("Id"), 1) FROM "Libraries"), EXISTS(SELECT 1 FROM "Libraries"));""",
            sql
        );
    }

    [Fact]
    public void BuildSequenceResetSql_KeepsTheSetvalCallOutOfAnyPerRowFrom()
    {
        string sql = DataMigrator.BuildSequenceResetSql("Chapters");

        // Both arguments are scalar subqueries, so setval runs once (the statement has no FROM of
        // its own) and is still applied on an empty table via EXISTS(...) = false.
        Assert.Contains("(SELECT COALESCE(MAX(\"Id\"), 1) FROM \"Chapters\")", sql);
        Assert.Contains("EXISTS(SELECT 1 FROM \"Chapters\")", sql);

        // The old per-row form ended the condition with `IS NOT NULL) FROM "Chapters"`.
        Assert.DoesNotContain("IS NOT NULL) FROM", sql);
    }
}
