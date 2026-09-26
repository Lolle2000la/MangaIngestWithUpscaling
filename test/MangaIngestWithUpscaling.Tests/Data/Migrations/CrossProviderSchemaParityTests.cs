using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Compares the database schema produced by each provider's migrations. The per-provider drift
/// guards assert each runtime model against its own snapshot, but not that the historical SQLite
/// migration chain and the fresh PostgreSQL baseline agree; this catches a divergence between the
/// two.
/// <para>
/// Compared: table names, column names and nullability; explicitly created (<c>IX_</c>) indexes
/// (columns and uniqueness, with expression indexes compared by name/uniqueness only); foreign keys
/// (parent table, child/parent columns and a normalised delete action); and a coarse per-column
/// type class plus default presence.
/// </para>
/// <para>
/// Intentionally excluded: precise column types/default values (SQLite's affinity and its historical
/// <c>ALTER TABLE ADD COLUMN</c> defaults differ from PostgreSQL's fresh baseline) and index/FK
/// names' SQL text. The remaining type/default differences are pinned to the explicit allow-list
/// below, so any <b>new</b> divergence fails the test.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class CrossProviderSchemaParityTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to compare migrated schemas across providers (Docker required).";

    /// <summary>
    /// Columns whose coarse <c>typeClass|hasDefault</c> signature legitimately differs between the
    /// SQLite migration chain and the PostgreSQL baseline. This list was generated from the actual
    /// migrated schemas and is pinned: any entry not listed here fails the test, so a new type or
    /// default divergence cannot slip through. The divergences are all systematic:
    /// <list type="bullet">
    /// <item>SQLite has no dedicated <c>timestamp</c>, <c>boolean</c>, <c>numeric</c> or <c>uuid</c>
    /// storage, so it maps <c>DateTime</c> to <c>TEXT</c>, <c>bool</c> to <c>INTEGER</c> and
    /// <c>ulong</c> to <c>INTEGER</c>.</item>
    /// <item>Timestamps added by the historical <c>ALTER TABLE ADD COLUMN</c> migration
    /// (<c>AddEntityTimestamps</c>) carry a SQLite default (<c>text|True</c>) that the fresh
    /// PostgreSQL baseline does not need (<c>datetime|False</c>).</item>
    /// <item><c>PersistedTasks.Order</c> and <c>UpscalerProfiles.Deleted</c> were likewise added with
    /// an <c>ALTER TABLE</c> default on SQLite.</item>
    /// </list>
    /// </summary>
    private static readonly string[] KnownTypeOrDefaultDivergences =
    [
        // SQLite stores DateTime as TEXT; PostgreSQL uses timestamptz.
        "ApiKeys.Expiration: sqlite=text|False, postgres=datetime|False",
        "AspNetUsers.LockoutEnd: sqlite=text|False, postgres=datetime|False",
        "ChapterSplitProcessingStates.ModifiedAt: sqlite=text|False, postgres=datetime|False",
        "Chapters.CreatedAt: sqlite=text|True, postgres=datetime|False",
        "Chapters.ModifiedAt: sqlite=text|True, postgres=datetime|False",
        "FilteredImages.DateAdded: sqlite=text|False, postgres=datetime|False",
        "FilteredImages.LastMatchedAt: sqlite=text|False, postgres=datetime|False",
        "Libraries.CreatedAt: sqlite=text|False, postgres=datetime|False",
        "Libraries.ModifiedAt: sqlite=text|False, postgres=datetime|False",
        "MangaAlternativeTitles.CreatedAt: sqlite=text|True, postgres=datetime|False",
        "MangaSeries.CreatedAt: sqlite=text|True, postgres=datetime|False",
        "MangaSeries.ModifiedAt: sqlite=text|True, postgres=datetime|False",
        "MergedChapterInfos.CreatedAt: sqlite=text|False, postgres=datetime|False",
        "PersistedTasks.CreatedAt: sqlite=text|False, postgres=datetime|False",
        "PersistedTasks.ProcessedAt: sqlite=text|False, postgres=datetime|False",
        "StripSplitFindings.CreatedAt: sqlite=text|False, postgres=datetime|False",
        "UpscalerProfiles.CreatedAt: sqlite=text|True, postgres=datetime|False",
        "UpscalerProfiles.ModifiedAt: sqlite=text|True, postgres=datetime|False",
        // SQLite stores bool as INTEGER.
        "ApiKeys.IsActive: sqlite=integer|False, postgres=boolean|False",
        "AspNetUsers.EmailConfirmed: sqlite=integer|False, postgres=boolean|False",
        "AspNetUsers.LockoutEnabled: sqlite=integer|False, postgres=boolean|False",
        "AspNetUsers.PhoneNumberConfirmed: sqlite=integer|False, postgres=boolean|False",
        "AspNetUsers.TwoFactorEnabled: sqlite=integer|False, postgres=boolean|False",
        "Chapters.IsUpscaled: sqlite=integer|False, postgres=boolean|False",
        "Libraries.MergeChapterParts: sqlite=integer|False, postgres=boolean|False",
        "Libraries.UpscaleOnIngest: sqlite=integer|False, postgres=boolean|False",
        "MangaSeries.MergeChapterParts: sqlite=integer|False, postgres=boolean|False",
        "MangaSeries.ShouldUpscale: sqlite=integer|False, postgres=boolean|False",
        // Soft-delete flag, added by ALTER TABLE with a default on SQLite.
        "UpscalerProfiles.Deleted: sqlite=integer|True, postgres=boolean|False",
        // ulong (PerceptualHash) maps to INTEGER on SQLite and numeric(20,0) on PostgreSQL.
        "FilteredImages.PerceptualHash: sqlite=integer|False, postgres=numeric|False",
        // Added by ALTER TABLE with a default on SQLite only.
        "PersistedTasks.Order: sqlite=integer|True, postgres=integer|False",
    ];

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

        Assert.Equal(postgresSchema.Keys.OrderBy(n => n), sqliteSchema.Keys.OrderBy(n => n));
        foreach (string table in postgresSchema.Keys)
        {
            Assert.Equal(postgresSchema[table], sqliteSchema[table]);
        }

        await AssertIndexesMatchAsync(sqlite, postgres, ct);
        await AssertForeignKeysMatchAsync(sqlite, postgres, ct);
        await AssertTypeAndDefaultSignaturesMatchAsync(sqlite, postgres, ct);
    }

    private static async Task AssertIndexesMatchAsync(
        TestDatabase sqlite,
        TestDatabase postgres,
        CancellationToken ct
    )
    {
        var (sqliteIndexes, sqliteExpressionIndexes) = await ReadSqliteIndexesAsync(sqlite, ct);
        var (postgresIndexes, postgresExpressionIndexes) = await ReadPostgresIndexesAsync(
            postgres,
            ct
        );

        Assert.NotEmpty(sqliteIndexes);
        Assert.NotEmpty(postgresIndexes);
        Assert.NotEmpty(sqliteExpressionIndexes);
        Assert.NotEmpty(postgresExpressionIndexes);

        // Explicitly created indexes carry their ordered key columns; expression indexes (the JSON
        // $type/ChapterId ones) report no column in SQLite, so only their name/uniqueness is
        // compared here. Their expressions are asserted in DatabaseMigrationTests.
        Assert.Equal(
            sqliteIndexes.OrderBy(i => i, StringComparer.Ordinal),
            postgresIndexes.OrderBy(i => i, StringComparer.Ordinal)
        );
        Assert.Equal(
            sqliteExpressionIndexes.OrderBy(i => i, StringComparer.Ordinal),
            postgresExpressionIndexes.OrderBy(i => i, StringComparer.Ordinal)
        );
    }

    private static async Task AssertForeignKeysMatchAsync(
        TestDatabase sqlite,
        TestDatabase postgres,
        CancellationToken ct
    )
    {
        HashSet<string> sqliteForeignKeys = await ReadSqliteForeignKeysAsync(sqlite, ct);
        HashSet<string> postgresForeignKeys = await ReadPostgresForeignKeysAsync(postgres, ct);

        Assert.NotEmpty(sqliteForeignKeys);
        Assert.NotEmpty(postgresForeignKeys);

        Assert.Equal(
            sqliteForeignKeys.OrderBy(fk => fk, StringComparer.Ordinal),
            postgresForeignKeys.OrderBy(fk => fk, StringComparer.Ordinal)
        );
    }

    private static async Task AssertTypeAndDefaultSignaturesMatchAsync(
        TestDatabase sqlite,
        TestDatabase postgres,
        CancellationToken ct
    )
    {
        SortedDictionary<string, string> sqliteSignatures = await ReadSqliteColumnSignaturesAsync(
            sqlite,
            ct
        );
        SortedDictionary<string, string> postgresSignatures =
            await ReadPostgresColumnSignaturesAsync(postgres, ct);

        Assert.NotEmpty(sqliteSignatures);
        Assert.NotEmpty(postgresSignatures);
        Assert.Equal(sqliteSignatures.Keys, postgresSignatures.Keys);

        // Provider-agnostic column-level divergence, e.g.
        // "PersistedTasks.Order: sqlite=integer|True, postgres=integer|False".
        var differences = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string key in sqliteSignatures.Keys)
        {
            if (sqliteSignatures[key] != postgresSignatures[key])
            {
                differences.Add(
                    $"{key}: sqlite={sqliteSignatures[key]}, postgres={postgresSignatures[key]}"
                );
            }
        }

        // Compare as sets so the allow-list's grouping/comments need not track sort order.
        Assert.Equal(
            KnownTypeOrDefaultDivergences.OrderBy(d => d, StringComparer.Ordinal),
            differences.ToArray()
        );
    }

    private static async Task MigrateAsync(TestDatabase database, CancellationToken ct)
    {
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        await context.Database.MigrateAsync(ct);
    }

    private static async Task<List<string>> ListSqliteTablesAsync(
        SqliteConnection connection,
        CancellationToken ct
    )
    {
        var tables = new List<string>();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' "
            + "AND name NOT LIKE 'sqlite_%' "
            + "AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock') "
            + "ORDER BY name";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<Dictionary<string, string[]>> ReadSqliteSchemaAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var schema = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string table in await ListSqliteTablesAsync(connection, ct))
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

    private static async Task<(
        HashSet<string> Indexes,
        HashSet<string> ExpressionIndexes
    )> ReadSqliteIndexesAsync(TestDatabase database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        var expressionIndexes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string table in await ListSqliteTablesAsync(connection, ct))
        {
            // PRAGMA index_list: seq, name, unique, origin, partial. 'c' means an explicitly created
            // index (as opposed to 'pk'/'u' constraint-backed ones); the model's indexes are all
            // named IX_*.
            var listed = new List<(string Name, bool Unique)>();
            await using (SqliteCommand list = connection.CreateCommand())
            {
                list.CommandText = $"PRAGMA index_list(\"{table}\")";
                await using SqliteDataReader reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string name = reader.GetString(1);
                    bool unique = reader.GetInt64(2) != 0;
                    string origin = reader.GetString(3);
                    if (origin == "c" && name.StartsWith("IX_", StringComparison.Ordinal))
                    {
                        listed.Add((name, unique));
                    }
                }
            }

            foreach ((string name, bool unique) in listed)
            {
                // PRAGMA index_info: seqno, cid, name. Expression keys report cid = -2 and a null
                // name, which cannot be compared against PostgreSQL's expression text here.
                var columns = new List<string>();
                bool isExpression = false;
                await using (SqliteCommand info = connection.CreateCommand())
                {
                    info.CommandText = $"PRAGMA index_info(\"{name}\")";
                    await using SqliteDataReader reader = await info.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        if (reader.IsDBNull(2))
                        {
                            isExpression = true;
                        }
                        else
                        {
                            columns.Add(reader.GetString(2));
                        }
                    }
                }

                if (isExpression)
                {
                    expressionIndexes.Add($"{table}|{name}|{unique}");
                }
                else
                {
                    indexes.Add($"{table}|{name}|{unique}|{string.Join(",", columns)}");
                }
            }
        }

        return (indexes, expressionIndexes);
    }

    private static async Task<(
        HashSet<string> Indexes,
        HashSet<string> ExpressionIndexes
    )> ReadPostgresIndexesAsync(TestDatabase database, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var rows =
            new List<(string Table, string Name, bool Unique, int Ordinal, string? Column)>();
        await using (NpgsqlCommand command = connection.CreateCommand())
        {
            // Constraint-backed indexes are excluded via the pg_constraint join. For expression
            // keys the pg_attribute join yields a null column (attnum = 0).
            command.CommandText = """
                SELECT c.relname, i.relname, ix.indisunique, k.ord, a.attname
                FROM pg_index ix
                JOIN pg_class i ON i.oid = ix.indexrelid
                JOIN pg_class c ON c.oid = ix.indrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_constraint con ON con.conindid = ix.indexrelid
                JOIN unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) ON true
                LEFT JOIN pg_attribute a
                  ON a.attrelid = ix.indrelid AND a.attnum = k.attnum
                WHERE n.nspname = 'public'
                  AND con.oid IS NULL
                  AND i.relname LIKE 'IX\_%' ESCAPE '\'
                  AND c.relname NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
                ORDER BY c.relname, i.relname, k.ord
                """;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(
                    (
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetBoolean(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)
                    )
                );
            }
        }

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        var expressionIndexes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(r => (r.Table, r.Name, r.Unique)))
        {
            var ordered = group.OrderBy(r => r.Ordinal).ToList();
            bool isExpression = ordered.Any(r => r.Column is null);
            if (isExpression)
            {
                expressionIndexes.Add($"{group.Key.Table}|{group.Key.Name}|{group.Key.Unique}");
            }
            else
            {
                indexes.Add(
                    $"{group.Key.Table}|{group.Key.Name}|{group.Key.Unique}"
                        + $"|{string.Join(",", ordered.Select(r => r.Column))}"
                );
            }
        }

        return (indexes, expressionIndexes);
    }

    private static async Task<HashSet<string>> ReadSqliteForeignKeysAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var foreignKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string table in await ListSqliteTablesAsync(connection, ct))
        {
            // PRAGMA foreign_key_list: id, seq, table, from, to, on_update, on_delete, match.
            // Rows of one (possibly composite) foreign key share an id and are ordered by seq.
            var rows =
                new List<(
                    long Id,
                    int Seq,
                    string Parent,
                    string Child,
                    string? ParentCol,
                    string OnDelete
                )>();
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list(\"{table}\")";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(
                        (
                            reader.GetInt64(0),
                            reader.GetInt32(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.GetString(6)
                        )
                    );
                }
            }

            foreach (var group in rows.GroupBy(r => r.Id))
            {
                var ordered = group.OrderBy(r => r.Seq).ToList();
                foreignKeys.Add(
                    DescribeForeignKey(
                        table,
                        ordered[0].Parent,
                        ordered.Select(r => r.Child).ToArray(),
                        ordered.Select(r => r.ParentCol ?? string.Empty).ToArray(),
                        NormalizeDeleteAction(ordered[0].OnDelete)
                    )
                );
            }
        }

        return foreignKeys;
    }

    private static async Task<HashSet<string>> ReadPostgresForeignKeysAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var foreignKeys = new HashSet<string>(StringComparer.Ordinal);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.relname,
                p.relname,
                con.confdeltype,
                (
                    SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM unnest(con.conkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                    JOIN pg_attribute a
                      ON a.attrelid = con.conrelid AND a.attnum = k.attnum
                ),
                (
                    SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM unnest(con.confkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                    JOIN pg_attribute a
                      ON a.attrelid = con.confrelid AND a.attnum = k.attnum
                )
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_class p ON p.oid = con.confrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'f' AND n.nspname = 'public'
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            foreignKeys.Add(
                DescribeForeignKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(3).Split(','),
                    reader.GetString(4).Split(','),
                    NormalizePostgresDeleteAction(reader.GetValue(2)?.ToString())
                )
            );
        }

        return foreignKeys;
    }

    private static async Task<SortedDictionary<string, string>> ReadSqliteColumnSignaturesAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var signatures = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string table in await ListSqliteTablesAsync(connection, ct))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // cid, name, type, notnull, dflt_value, pk
                string column = reader.GetString(1);
                string typeClass = ClassifySqliteType(reader.GetString(2));
                bool hasDefault = !reader.IsDBNull(4);
                signatures[$"{table}.{column}"] = $"{typeClass}|{hasDefault}";
            }
        }

        return signatures;
    }

    private static async Task<SortedDictionary<string, string>> ReadPostgresColumnSignaturesAsync(
        TestDatabase database,
        CancellationToken ct
    )
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        var signatures = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name, column_name, data_type, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);
            string typeClass = ClassifyPostgresType(reader.GetString(2));
            bool hasDefault = !reader.IsDBNull(3);
            signatures[$"{table}.{column}"] = $"{typeClass}|{hasDefault}";
        }

        return signatures;
    }

    private static string DescribeForeignKey(
        string childTable,
        string parentTable,
        IReadOnlyList<string> childColumns,
        IReadOnlyList<string> parentColumns,
        string deleteAction
    ) =>
        $"{childTable}|{parentTable}|{string.Join(",", childColumns)}"
        + $"|{string.Join(",", parentColumns)}|{deleteAction}";

    private static string NormalizeDeleteAction(string action) =>
        action.Trim().ToUpperInvariant() switch
        {
            "CASCADE" => "CASCADE",
            "SET NULL" => "SET_NULL",
            "SET DEFAULT" => "SET_DEFAULT",
            "RESTRICT" or "NO ACTION" => "RESTRICT_OR_NO_ACTION",
            _ => throw new InvalidOperationException($"Unknown delete action '{action}'."),
        };

    private static string NormalizePostgresDeleteAction(string? code) =>
        code switch
        {
            "c" => "CASCADE",
            "n" => "SET_NULL",
            "d" => "SET_DEFAULT",
            "r" or "a" => "RESTRICT_OR_NO_ACTION",
            _ => throw new InvalidOperationException($"Unknown delete action code '{code}'."),
        };

    /// <summary>
    /// Collapses a declared SQLite type into one of the coarse classes used for cross-provider
    /// comparison. SQLite's type affinity loses information (a <c>DateTime</c>, <c>Guid</c>,
    /// <c>decimal</c> and <c>string</c> all become <c>TEXT</c>), so several classes never appear on
    /// the SQLite side and show up in <see cref="KnownTypeOrDefaultDivergences"/>.
    /// </summary>
    private static string ClassifySqliteType(string declaredType)
    {
        string type = declaredType.Trim().ToLowerInvariant();
        if (type.Length == 0 || type.Contains("blob"))
        {
            return "blob";
        }
        if (type.Contains("int"))
        {
            return "integer";
        }
        if (type.Contains("char") || type.Contains("clob") || type.Contains("text"))
        {
            return "text";
        }
        if (type.Contains("real") || type.Contains("floa") || type.Contains("doub"))
        {
            return "float";
        }
        if (type.Contains("bool"))
        {
            return "boolean";
        }
        if (type.Contains("date") || type.Contains("time"))
        {
            return "datetime";
        }
        if (type.Contains("json"))
        {
            return "json";
        }
        if (type.Contains("numeric") || type.Contains("decimal"))
        {
            return "numeric";
        }
        return "other:" + type;
    }

    /// <summary>Collapses a PostgreSQL <c>data_type</c> into the same coarse classes.</summary>
    private static string ClassifyPostgresType(string dataType) =>
        dataType.Trim().ToLowerInvariant() switch
        {
            "smallint" or "integer" or "bigint" => "integer",
            "boolean" => "boolean",
            "text" or "character varying" or "character" => "text",
            "json" or "jsonb" => "json",
            "numeric" or "decimal" or "money" => "numeric",
            "timestamp with time zone"
            or "timestamp without time zone"
            or "timestamp"
            or "date"
            or "time with time zone"
            or "time without time zone"
            or "time" => "datetime",
            "real" or "double precision" or "float" => "float",
            "bytea" => "blob",
            "uuid" => "guid",
            string other => "other:" + other,
        };

    private static string Describe(string column, bool notNull) =>
        $"{column}:{(notNull ? "NOT NULL" : "NULL")}";
}
