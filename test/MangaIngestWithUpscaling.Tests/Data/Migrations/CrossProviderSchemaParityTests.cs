using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Compares the database schema produced by each provider's migrations. The per-provider drift
/// guards assert each runtime model against its own snapshot, but not that the historical SQLite
/// migration chain and the fresh PostgreSQL baseline agree; this catches a table, column or
/// nullability divergence between the two. Provider-specific column types are intentionally not
/// compared (SQLite has no equivalent of <c>timestamptz</c>/<c>boolean</c>/<c>numeric</c>).
/// </summary>
[Trait("Category", "Integration")]
public class CrossProviderSchemaParityTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to compare migrated schemas across providers (Docker required).";

    [Fact]
    public async Task MigratedSchemas_AgreeOnTablesColumnsAndNullability()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqlite = TestDatabaseFactory.Create(TestDatabaseBackend.Sqlite);
        await using TestDatabase postgres = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        await MigrateAsync(sqlite, ct);
        await MigrateAsync(postgres, ct);

        Dictionary<string, string[]> sqliteSchema = await ReadSqliteSchemaAsync(sqlite, ct);
        Dictionary<string, string[]> postgresSchema = await ReadPostgresSchemaAsync(postgres, ct);

        // Guard against a vacuous pass: two empty schema maps would trivially satisfy the equality
        // checks below.
        Assert.NotEmpty(sqliteSchema);
        Assert.NotEmpty(postgresSchema);

        // Scope: only table names, column names and nullability are compared. Provider-specific
        // column types and defaults are deliberately excluded (SQLite has no equivalent of
        // timestamptz/boolean/numeric, and its ALTER TABLE defaults differ from the fresh PostgreSQL
        // baseline), so this is an intentional decision, not an omission.
        Assert.Equal(postgresSchema.Keys.OrderBy(n => n), sqliteSchema.Keys.OrderBy(n => n));
        foreach (string table in postgresSchema.Keys)
        {
            Assert.Equal(postgresSchema[table], sqliteSchema[table]);
        }
    }

    private static async Task MigrateAsync(TestDatabase database, CancellationToken ct)
    {
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        await context.Database.MigrateAsync(ct);
    }

    private static async Task<Dictionary<string, string[]>> ReadSqliteSchemaAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var tables = new List<string>();
        await using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' "
                + "AND name NOT LIKE 'sqlite_%' "
                + "AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')";
            await using SqliteDataReader reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var schema = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

            var columns = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                // cid, name, type, notnull, dflt_value, pk
                string name = reader.GetString(1);
                bool notNull = reader.GetInt64(3) != 0;
                bool isPrimaryKey = reader.GetInt64(5) != 0;
                columns.Add(Describe(name, isPrimaryKey || notNull));
            }

            schema[table] = columns.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        }

        return schema;
    }

    private static async Task<Dictionary<string, string[]>> ReadPostgresSchemaAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var primaryKeys = new HashSet<(string Table, string Column)>();
        await using (NpgsqlCommand primaryKeyCommand = connection.CreateCommand())
        {
            primaryKeyCommand.CommandText = """
                SELECT tc.table_name, kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name
                 AND tc.table_schema = kcu.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = 'public'
                """;
            await using NpgsqlDataReader reader = await primaryKeyCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                primaryKeys.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var byTable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (NpgsqlCommand columnsCommand = connection.CreateCommand())
        {
            columnsCommand.CommandText = """
                SELECT table_name, column_name, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
                """;
            await using NpgsqlDataReader reader = await columnsCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string table = reader.GetString(0);
                string column = reader.GetString(1);
                bool nullable =
                    reader.GetString(2) == "YES" && !primaryKeys.Contains((table, column));

                if (!byTable.TryGetValue(table, out List<string>? columns))
                {
                    byTable[table] = columns = [];
                }

                columns.Add(Describe(column, notNull: !nullable));
            }
        }

        return byTable.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal
        );
    }

    private static string Describe(string column, bool notNull) =>
        $"{column}:{(notNull ? "NOT NULL" : "NULL")}";
}
