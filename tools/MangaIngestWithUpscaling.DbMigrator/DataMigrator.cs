using System.Reflection;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LogModel;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MangaIngestWithUpscaling.DbMigrator;

/// <summary>Options controlling a single provider-to-provider data migration.</summary>
public sealed record MigratorOptions(
    DatabaseProvider FromProvider,
    string FromConnection,
    DatabaseProvider ToProvider,
    string ToConnection,
    int BatchSize,
    bool Force,
    bool IncludeLogs,
    string? FromLogsConnection,
    string? ToLogsConnection
);

/// <summary>
/// Copies an installation's data from one database provider to another. Kept separate from the CLI
/// entry point so it can be exercised by tests.
/// </summary>
public static class DataMigrator
{
    public static async Task MigrateAsync(
        MigratorOptions options,
        Action<string> log,
        CancellationToken cancellationToken = default
    )
    {
        if (options.FromProvider == options.ToProvider)
        {
            throw new InvalidOperationException("Source and target providers must differ.");
        }

        EnsureSqliteSourceFileExists(options.FromConnection, options.FromProvider);

        // The task payload is polymorphic JSON whose concrete types live in the web assembly.
        TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);

        log(
            $"Migrating {options.FromProvider} -> {options.ToProvider} "
                + $"(batch size {options.BatchSize})..."
        );

        await using ApplicationDbContext source = CreateApplicationContext(
            options.FromProvider,
            options.FromConnection
        );
        await using ApplicationDbContext target = CreateApplicationContext(
            options.ToProvider,
            options.ToConnection
        );

        log("Applying migrations to the target...");
        await target.Database.MigrateAsync(cancellationToken);

        List<TableOperation> tables = BuildTableOperations(
            source,
            target,
            options.BatchSize,
            log,
            cancellationToken
        );

        if (!options.Force)
        {
            foreach (TableOperation table in tables)
            {
                if (await table.HasData())
                {
                    throw new InvalidOperationException(
                        $"Target table '{table.Name}' is not empty. Use a fresh database or pass --force."
                    );
                }
            }
        }

        // Resolve and validate the logs target before anything is written, so a refusal (or a
        // misconfigured logs connection) cannot leave the application tables half migrated.
        LogsMigrationPlan? logsPlan = null;
        if (options.IncludeLogs)
        {
            logsPlan = await PrepareLogsMigrationAsync(options, log, cancellationToken);
        }

        if (options.Force)
        {
            log("Clearing the target database...");
            for (int i = tables.Count - 1; i >= 0; i--)
            {
                await tables[i].Clear();
            }
        }

        foreach (TableOperation table in tables)
        {
            await table.Copy();
        }

        if (options.ToProvider == DatabaseProvider.Postgres)
        {
            log("Resetting PostgreSQL sequences...");
            await ResetPostgresSequencesAsync(options.ToConnection, cancellationToken);
        }

        if (logsPlan is not null)
        {
            await MigrateLogsAsync(options, logsPlan, log, cancellationToken);
        }

        log("Migration completed successfully.");
    }

    /// <summary>
    /// Rejects a SQLite source that does not exist before any context is created. The default SQLite
    /// open mode (<c>ReadWriteCreate</c>) would otherwise silently create an empty database at a
    /// mistyped path, and the migration would run against an empty source as if it had succeeded.
    /// PostgreSQL sources are left to the provider to validate.
    /// </summary>
    private static void EnsureSqliteSourceFileExists(
        string connectionString,
        DatabaseProvider provider
    )
    {
        if (provider != DatabaseProvider.Sqlite)
        {
            return;
        }

        if (ResolveSqliteFile(connectionString) is { Exists: false } missing)
        {
            throw new InvalidOperationException(
                $"SQLite source database file '{missing.Path}' does not exist. "
                    + "Check the --from-connection value."
            );
        }
    }

    /// <summary>
    /// Resolves the file backing a SQLite connection string. Returns <c>null</c> for in-memory
    /// databases (which have no file), otherwise the file path and whether it exists.
    /// </summary>
    private static (string Path, bool Exists)? ResolveSqliteFile(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory)
        {
            return null;
        }

        string dataSource = builder.DataSource;
        if (string.IsNullOrEmpty(dataSource))
        {
            return null;
        }

        // Microsoft.Data.Sqlite passes "file:" URIs through to SQLite and the builder does not
        // surface their query parameters (for example mode=memory), so parse them here.
        string path = dataSource;
        string? query = null;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
            int questionMark = path.IndexOf('?');
            if (questionMark >= 0)
            {
                query = path[(questionMark + 1)..];
                path = path[..questionMark];
            }
        }

        if (
            path.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || (
                query is not null
                && query
                    .Split('&')
                    .Any(part => part.Equals("mode=memory", StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            return null;
        }

        return (path, File.Exists(path));
    }

    private static ApplicationDbContext CreateApplicationContext(
        DatabaseProvider provider,
        string connection
    )
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        DatabaseSetup.UseDatabaseProvider(
            optionsBuilder,
            provider,
            connection,
            MigrationsAssembly(provider)
        );
        return new ApplicationDbContext(optionsBuilder.Options) { SkipTimestampUpdates = true };
    }

    private static LoggingDbContext CreateLoggingContext(
        DatabaseProvider provider,
        string connection
    )
    {
        var optionsBuilder = new DbContextOptionsBuilder<LoggingDbContext>();
        DatabaseSetup.UseDatabaseProvider(
            optionsBuilder,
            provider,
            connection,
            MigrationsAssembly(provider)
        );
        return new LoggingDbContext(optionsBuilder.Options);
    }

    private static string MigrationsAssembly(DatabaseProvider provider) =>
        provider == DatabaseProvider.Sqlite
            ? typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
            : typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!;

    internal static List<TableOperation> BuildTableOperations(
        ApplicationDbContext source,
        ApplicationDbContext target,
        int batchSize,
        Action<string> log,
        CancellationToken cancellationToken
    )
    {
        var operations = new List<TableOperation>();

        void Add<T>(
            string name,
            Func<ApplicationDbContext, IQueryable<T>> query,
            DbSet<T> targetSet,
            bool ignoreQueryFilters = false
        )
            where T : class
        {
            IQueryable<T> SourceQuery(ApplicationDbContext context) =>
                ignoreQueryFilters ? query(context).IgnoreQueryFilters() : query(context);

            operations.Add(
                new TableOperation(
                    name,
                    async () => await SourceQuery(target).AnyAsync(cancellationToken),
                    async () => await SourceQuery(target).ExecuteDeleteAsync(cancellationToken),
                    async () =>
                        await CopyAsync(
                            source,
                            target,
                            SourceQuery,
                            targetSet,
                            name,
                            batchSize,
                            log,
                            cancellationToken
                        )
                )
            );
        }

        // Parents before children so that a --force clear can run in reverse order.
        Add("AspNetRoles", c => c.Roles, target.Roles);
        Add("AspNetUsers", c => c.Users, target.Users);
        Add(
            "UpscalerProfiles",
            c => c.UpscalerProfiles,
            target.UpscalerProfiles,
            ignoreQueryFilters: true
        );
        Add("Libraries", c => c.Libraries, target.Libraries);
        Add("LibraryIngestPaths", c => c.LibraryIngestPaths, target.LibraryIngestPaths);
        Add("LibraryFilterRules", c => c.LibraryFilterRules, target.LibraryFilterRules);
        Add("LibraryRenameRules", c => c.LibraryRenameRules, target.LibraryRenameRules);
        Add("MangaSeries", c => c.MangaSeries, target.MangaSeries);
        Add("MangaAlternativeTitles", c => c.MangaAlternativeTitles, target.MangaAlternativeTitles);
        Add("Chapters", c => c.Chapters, target.Chapters);
        Add("MergedChapterInfos", c => c.MergedChapterInfos, target.MergedChapterInfos);
        Add("FilteredImages", c => c.FilteredImages, target.FilteredImages);
        Add(
            "ChapterSplitProcessingStates",
            c => c.ChapterSplitProcessingStates,
            target.ChapterSplitProcessingStates
        );
        Add("StripSplitFindings", c => c.StripSplitFindings, target.StripSplitFindings);
        Add("PersistedTasks", c => c.PersistedTasks, target.PersistedTasks);
        Add("ApiKeys", c => c.ApiKeys, target.ApiKeys);
        Add("DataProtectionKeys", c => c.DataProtectionKeys, target.DataProtectionKeys);
        Add("AspNetRoleClaims", c => c.RoleClaims, target.RoleClaims);
        Add("AspNetUserClaims", c => c.UserClaims, target.UserClaims);
        Add("AspNetUserLogins", c => c.UserLogins, target.UserLogins);
        Add("AspNetUserRoles", c => c.UserRoles, target.UserRoles);
        Add("AspNetUserTokens", c => c.UserTokens, target.UserTokens);

        return operations;
    }

    /// <summary>
    /// Streams the source rows and inserts them in batches. Streaming (instead of <c>Skip/Take</c>)
    /// matters because paging without an <c>ORDER BY</c> is not stable on PostgreSQL, which silently
    /// skips or repeats rows.
    /// </summary>
    private static async Task CopyAsync<T>(
        ApplicationDbContext source,
        ApplicationDbContext target,
        Func<ApplicationDbContext, IQueryable<T>> query,
        DbSet<T> targetSet,
        string name,
        int batchSize,
        Action<string> log,
        CancellationToken cancellationToken
    )
        where T : class
    {
        int total = await query(source).CountAsync(cancellationToken);
        if (total == 0)
        {
            log($"{name}: nothing to migrate.");
            return;
        }

        log($"{name}: migrating {total} rows...");
        int copied = 0;
        var batch = new List<T>(batchSize);

        await foreach (
            T entity in query(source)
                .AsNoTracking()
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            NormalizeUtcDateTimes(entity);
            batch.Add(entity);

            if (batch.Count >= batchSize)
            {
                copied += await FlushBatchAsync(target, targetSet, batch, cancellationToken);
                log($"  {name}: {copied}/{total}");
            }
        }

        if (batch.Count > 0)
        {
            copied += await FlushBatchAsync(target, targetSet, batch, cancellationToken);
            log($"  {name}: {copied}/{total}");
        }
    }

    private static async Task<int> FlushBatchAsync<T>(
        DbContext target,
        DbSet<T> targetSet,
        List<T> batch,
        CancellationToken cancellationToken
    )
        where T : class
    {
        int count = batch.Count;
        await targetSet.AddRangeAsync(batch, cancellationToken);
        await target.SaveChangesAsync(cancellationToken);
        target.ChangeTracker.Clear();
        batch.Clear();
        return count;
    }

    /// <summary>
    /// SQLite stores <see cref="DateTime"/> as text without a kind, so values read back are
    /// <see cref="DateTimeKind.Unspecified"/>. PostgreSQL rejects those for <c>timestamp with time
    /// zone</c> columns, so mark them as UTC (the application always persists UTC timestamps).
    /// <see cref="DateTimeKind.Local"/> values are converted (not relabelled) so the instant is
    /// preserved, and <see cref="DateTimeOffset"/> values are normalized to UTC as well.
    /// </summary>
    /// <remarks>
    /// Only the entity's own scalar properties are visited. EF complex/owned values that are not
    /// surfaced as top-level properties (there are none in the current model) would not be reached;
    /// add handling here if such a member is introduced.
    /// </remarks>
    internal static void NormalizeUtcDateTimes(object entity)
    {
        foreach (
            PropertyInfo property in entity
                .GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        )
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            if (property.PropertyType == typeof(DateTime))
            {
                var value = (DateTime)property.GetValue(entity)!;
                if (value.Kind != DateTimeKind.Utc)
                {
                    property.SetValue(entity, ToUtc(value));
                }
            }
            else if (property.PropertyType == typeof(DateTime?))
            {
                var value = (DateTime?)property.GetValue(entity);
                if (value is { Kind: not DateTimeKind.Utc })
                {
                    property.SetValue(entity, ToUtc(value.Value));
                }
            }
            else if (property.PropertyType == typeof(DateTimeOffset))
            {
                // Npgsql sends the UTC instant regardless, but normalizing keeps the copied value
                // canonical (for example IdentityUser.LockoutEnd).
                var value = (DateTimeOffset)property.GetValue(entity)!;
                property.SetValue(entity, value.ToUniversalTime());
            }
            else if (property.PropertyType == typeof(DateTimeOffset?))
            {
                var value = (DateTimeOffset?)property.GetValue(entity);
                if (value is { } offset)
                {
                    property.SetValue(entity, offset.ToUniversalTime());
                }
            }
        }
    }

    /// <summary>
    /// Normalizes a value read from the source to UTC. <see cref="DateTimeKind.Local"/> is converted
    /// so its instant is preserved; unspecified values (SQLite) are relabelled, matching the
    /// application's convention of storing UTC.
    /// </summary>
    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static async Task MigrateLogsAsync(
        MigratorOptions options,
        LogsMigrationPlan plan,
        Action<string> log,
        CancellationToken cancellationToken
    )
    {
        if (plan.SourceCount is not int total)
        {
            // PrepareLogsMigrationAsync already logged that the source logs store is unavailable.
            return;
        }

        await using LoggingDbContext sourceLogs = CreateLoggingContext(
            options.FromProvider,
            plan.FromLogsConnection
        );
        await using LoggingDbContext targetLogs = CreateLoggingContext(
            options.ToProvider,
            plan.ToLogsConnection
        );

        int targetCount = await targetLogs.LogEntries.CountAsync(cancellationToken);
        if (targetCount > 0)
        {
            // PrepareLogsMigrationAsync only lets execution reach here with --force, so these are
            // the rows the explicit ids below would otherwise collide with.
            log($"Logs: clearing {targetCount} existing rows in the target...");
            await targetLogs.LogEntries.ExecuteDeleteAsync(cancellationToken);
        }

        if (total == 0)
        {
            log("Logs: nothing to migrate.");
            return;
        }

        log($"Logs: migrating {total} rows...");
        int copied = 0;
        var batch = new List<Log>(options.BatchSize);

        await foreach (
            Log logEntry in sourceLogs
                .LogEntries.AsNoTracking()
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            NormalizeUtcDateTimes(logEntry);
            batch.Add(logEntry);

            if (batch.Count >= options.BatchSize)
            {
                copied += await FlushBatchAsync(
                    targetLogs,
                    targetLogs.LogEntries,
                    batch,
                    cancellationToken
                );
                log($"  Logs: {copied}/{total}");
            }
        }

        if (batch.Count > 0)
        {
            copied += await FlushBatchAsync(
                targetLogs,
                targetLogs.LogEntries,
                batch,
                cancellationToken
            );
            log($"  Logs: {copied}/{total}");
        }

        if (options.ToProvider == DatabaseProvider.Postgres)
        {
            // Unlike the application tables, the target log table is not covered by the earlier
            // reset. PostgreSQL identity columns do not advance when rows are inserted with explicit
            // ids, so without this the first log the application writes reuses an id and collides.
            log("Logs: resetting the PostgreSQL identity sequence...");
            await ResetPostgresSequenceAsync(plan.ToLogsConnection, "Logs", cancellationToken);
        }
    }

    /// <summary>
    /// Resolves the logs connections and validates the target before any application table is
    /// written. Returns a plan consumed by <see cref="MigrateLogsAsync"/> after the copy, so a
    /// refusal cannot leave the target half migrated.
    /// </summary>
    private static async Task<LogsMigrationPlan> PrepareLogsMigrationAsync(
        MigratorOptions options,
        Action<string> log,
        CancellationToken cancellationToken
    )
    {
        string fromLogs = ResolveLogsConnection(
            options.FromProvider,
            options.FromConnection,
            options.FromLogsConnection,
            "from-logs-connection"
        );
        string toLogs = ResolveLogsConnection(
            options.ToProvider,
            options.ToConnection,
            options.ToLogsConnection,
            "to-logs-connection"
        );

        await using LoggingDbContext sourceLogs = CreateLoggingContext(
            options.FromProvider,
            fromLogs
        );

        if (
            options.FromProvider == DatabaseProvider.Sqlite
            && ResolveSqliteFile(fromLogs) is { Exists: false }
        )
        {
            log("Logs: source SQLite logs database not found, skipping.");
            return new LogsMigrationPlan(fromLogs, toLogs, SourceCount: null);
        }

        int sourceCount;
        try
        {
            sourceCount = await sourceLogs.LogEntries.AsNoTracking().CountAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The application creates the PostgreSQL Logs table on startup; a source that never ran
            // with logging enabled has nothing to copy.
            log("Logs: source PostgreSQL logs table does not exist, skipping.");
            return new LogsMigrationPlan(fromLogs, toLogs, SourceCount: null);
        }

        await using LoggingDbContext targetLogs = CreateLoggingContext(options.ToProvider, toLogs);
        await EnsureLogsTableAsync(options.ToProvider, targetLogs, toLogs, cancellationToken);

        int targetCount = await targetLogs.LogEntries.CountAsync(cancellationToken);
        if (targetCount > 0 && !options.Force)
        {
            // Checked before the application tables are copied so the refusal is not destructive.
            throw new InvalidOperationException(
                "Target table 'Logs' is not empty. Use a fresh database or pass --force."
            );
        }

        return new LogsMigrationPlan(fromLogs, toLogs, sourceCount);
    }

    private static string ResolveLogsConnection(
        DatabaseProvider provider,
        string mainConnection,
        string? explicitConnection,
        string optionName
    )
    {
        // For PostgreSQL the logs live in the main database; for SQLite they live in a separate file.
        if (provider == DatabaseProvider.Postgres)
        {
            return mainConnection;
        }

        if (string.IsNullOrWhiteSpace(explicitConnection))
        {
            throw new InvalidOperationException(
                $"The '{optionName}' option is required to migrate logs for a SQLite database."
            );
        }

        return explicitConnection;
    }

    private static async Task EnsureLogsTableAsync(
        DatabaseProvider provider,
        LoggingDbContext targetLogs,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        if (provider == DatabaseProvider.Sqlite)
        {
            // EnsureCreated cannot be used here: opening a SQLite file creates it, which makes EF
            // think the database already exists and skip table creation. Use explicit DDL matching
            // the Serilog SQLite sink's schema instead.
            await targetLogs.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "Logs" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "Timestamp" TEXT NOT NULL,
                    "Level" TEXT,
                    "Exception" TEXT,
                    "RenderedMessage" TEXT,
                    "Properties" TEXT
                );
                """,
                cancellationToken
            );
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = PostgresLogging.CreateTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The application tables whose PostgreSQL <c>Id</c> column is an integer identity column, so
    /// their sequences must be advanced after copying rows with explicit ids. Kept next to the copy
    /// list and guarded by <c>MigratorTableCoverageTests</c>, which compares it to the EF model.
    /// <c>Logs</c> is handled separately in <see cref="MigrateLogsAsync"/> (it is not part of the
    /// application context).
    /// </summary>
    internal static readonly string[] PostgresIdentityTables =
    {
        "ApiKeys",
        "AspNetRoleClaims",
        "AspNetUserClaims",
        "ChapterSplitProcessingStates",
        "Chapters",
        "DataProtectionKeys",
        "FilteredImages",
        "Libraries",
        "LibraryFilterRules",
        "LibraryIngestPaths",
        "LibraryRenameRules",
        "MangaSeries",
        "MergedChapterInfos",
        "PersistedTasks",
        "StripSplitFindings",
        "UpscalerProfiles",
    };

    private static async Task ResetPostgresSequencesAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string table in PostgresIdentityTables)
        {
            await ResetPostgresSequenceAsync(connection, table, cancellationToken);
        }
    }

    /// <summary>
    /// Advances a PostgreSQL identity sequence to the current maximum id, so rows inserted with
    /// explicit ids (the migrator copies existing ids) do not collide with generated ones.
    /// <paramref name="table" /> is always a hard-coded table name, never user input.
    /// </summary>
    private static async Task ResetPostgresSequenceAsync(
        NpgsqlConnection connection,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT setval(pg_get_serial_sequence('\"{table}\"', 'Id'), "
            + $"COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"{table}\";";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ResetPostgresSequenceAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ResetPostgresSequenceAsync(connection, table, cancellationToken);
    }
}

/// <summary>One table's migrate operation: presence check, clear and copy.</summary>
internal sealed record TableOperation(
    string Name,
    Func<Task<bool>> HasData,
    Func<Task> Clear,
    Func<Task> Copy
);

/// <summary>
/// The resolved connections for a logs migration. <paramref name="SourceCount" /> is <c>null</c>
/// when the source logs store is unavailable and the migration should be skipped.
/// </summary>
internal sealed record LogsMigrationPlan(
    string FromLogsConnection,
    string ToLogsConnection,
    int? SourceCount
);
