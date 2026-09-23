using System.Globalization;
using System.Reflection;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LogModel;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// The task payload is polymorphic JSON whose concrete types live in the web assembly.
TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);

Dictionary<string, string> options = ParseArgs(args);
if (
    !options.TryGetValue("from", out string? fromProviderName)
    || !options.TryGetValue("to", out string? toProviderName)
    || !options.TryGetValue("from-connection", out string? fromConnection)
    || !options.TryGetValue("to-connection", out string? toConnection)
)
{
    PrintUsage();
    return 1;
}

if (
    !TryParseProvider(fromProviderName, out DatabaseProvider fromProvider)
    || !TryParseProvider(toProviderName, out DatabaseProvider toProvider)
)
{
    Console.Error.WriteLine("Unknown provider. Use 'sqlite' or 'postgres'.");
    return 1;
}

if (fromProvider == toProvider)
{
    Console.Error.WriteLine("Source and target providers must differ.");
    return 1;
}

int batchSize =
    options.TryGetValue("batch-size", out string? batch)
    && int.TryParse(batch, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
    && parsed > 0
        ? parsed
        : 500;
bool force = options.ContainsKey("force");
bool includeLogs = options.ContainsKey("include-logs");

Console.WriteLine($"Migrating {fromProvider} -> {toProvider} (batch size {batchSize})...");

await using ApplicationDbContext source = CreateApplicationContext(fromProvider, fromConnection);
await using ApplicationDbContext target = CreateApplicationContext(toProvider, toConnection);

Console.WriteLine("Applying migrations to the target...");
await target.Database.MigrateAsync();

List<TableOperation> tables = BuildTableOperations(source, target, batchSize);

if (force)
{
    Console.WriteLine("Clearing the target database...");
    for (int i = tables.Count - 1; i >= 0; i--)
    {
        await tables[i].Clear();
    }
}
else
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

foreach (TableOperation table in tables)
{
    await table.Copy();
}

if (toProvider == DatabaseProvider.Postgres)
{
    Console.WriteLine("Resetting PostgreSQL sequences...");
    await ResetPostgresSequencesAsync(toConnection);
}

if (includeLogs)
{
    await MigrateLogsAsync(
        fromProvider,
        fromConnection,
        toProvider,
        toConnection,
        options,
        batchSize
    );
}

Console.WriteLine("Migration completed successfully.");
return 0;

static ApplicationDbContext CreateApplicationContext(DatabaseProvider provider, string connection)
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

static LoggingDbContext CreateLoggingContext(DatabaseProvider provider, string connection)
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

static string MigrationsAssembly(DatabaseProvider provider) =>
    provider == DatabaseProvider.Sqlite
        ? typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
        : typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!;

static List<TableOperation> BuildTableOperations(
    ApplicationDbContext source,
    ApplicationDbContext target,
    int batchSize
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
                async () => await SourceQuery(target).AnyAsync(),
                async () => await SourceQuery(target).ExecuteDeleteAsync(),
                async () => await CopyAsync(source, target, SourceQuery, targetSet, name, batchSize)
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

static async Task CopyAsync<T>(
    ApplicationDbContext source,
    ApplicationDbContext target,
    Func<ApplicationDbContext, IQueryable<T>> query,
    DbSet<T> targetSet,
    string name,
    int batchSize
)
    where T : class
{
    int total = await query(source).CountAsync();
    if (total == 0)
    {
        Console.WriteLine($"{name}: nothing to migrate.");
        return;
    }

    Console.WriteLine($"{name}: migrating {total} rows...");
    int copied = 0;
    while (copied < total)
    {
        List<T> batch = await query(source)
            .AsNoTracking()
            .Skip(copied)
            .Take(batchSize)
            .ToListAsync();

        if (batch.Count == 0)
        {
            break;
        }

        foreach (T entity in batch)
        {
            NormalizeUtcDateTimes(entity);
        }

        await targetSet.AddRangeAsync(batch);
        await target.SaveChangesAsync();
        target.ChangeTracker.Clear();

        copied += batch.Count;
        Console.WriteLine($"  {name}: {copied}/{total}");
    }
}

/// <summary>
/// SQLite stores <see cref="DateTime"/> as text without a kind, so values read back are
/// <see cref="DateTimeKind.Unspecified"/>. PostgreSQL rejects those for <c>timestamp with time
/// zone</c> columns, so mark them as UTC (the application always persists UTC timestamps).
/// </summary>
static void NormalizeUtcDateTimes(object entity)
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
                property.SetValue(entity, DateTime.SpecifyKind(value, DateTimeKind.Utc));
            }
        }
        else if (property.PropertyType == typeof(DateTime?))
        {
            var value = (DateTime?)property.GetValue(entity);
            if (value is { Kind: not DateTimeKind.Utc })
            {
                property.SetValue(entity, DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
            }
        }
    }
}

static async Task MigrateLogsAsync(
    DatabaseProvider fromProvider,
    string fromConnection,
    DatabaseProvider toProvider,
    string toConnection,
    Dictionary<string, string> options,
    int batchSize
)
{
    string fromLogs = ResolveLogsConnection(
        fromProvider,
        fromConnection,
        options,
        "from-logs-connection"
    );
    string toLogs = ResolveLogsConnection(toProvider, toConnection, options, "to-logs-connection");

    await using LoggingDbContext sourceLogs = CreateLoggingContext(fromProvider, fromLogs);
    await using LoggingDbContext targetLogs = CreateLoggingContext(toProvider, toLogs);

    if (fromProvider == DatabaseProvider.Sqlite && !await sourceLogs.Database.CanConnectAsync())
    {
        Console.WriteLine("Logs: source SQLite logs database not found, skipping.");
        return;
    }

    await EnsureLogsTableAsync(toProvider, targetLogs, toLogs);

    int total = await sourceLogs.LogEntries.AsNoTracking().CountAsync();
    if (total == 0)
    {
        Console.WriteLine("Logs: nothing to migrate.");
        return;
    }

    Console.WriteLine($"Logs: migrating {total} rows...");
    int copied = 0;
    while (copied < total)
    {
        List<Log> batch = await sourceLogs
            .LogEntries.AsNoTracking()
            .Skip(copied)
            .Take(batchSize)
            .ToListAsync();

        if (batch.Count == 0)
        {
            break;
        }

        foreach (Log log in batch)
        {
            NormalizeUtcDateTimes(log);
        }

        await targetLogs.LogEntries.AddRangeAsync(batch);
        await targetLogs.SaveChangesAsync();
        targetLogs.ChangeTracker.Clear();

        copied += batch.Count;
        Console.WriteLine($"  Logs: {copied}/{total}");
    }
}

static string ResolveLogsConnection(
    DatabaseProvider provider,
    string mainConnection,
    Dictionary<string, string> options,
    string optionName
)
{
    // For PostgreSQL the logs live in the main database; for SQLite they live in a separate file.
    if (provider == DatabaseProvider.Postgres)
    {
        return mainConnection;
    }

    if (!options.TryGetValue(optionName, out string? logsConnection))
    {
        throw new InvalidOperationException(
            $"The '{optionName}' option is required to migrate logs for a SQLite database."
        );
    }

    return logsConnection;
}

static async Task EnsureLogsTableAsync(
    DatabaseProvider provider,
    LoggingDbContext targetLogs,
    string connectionString
)
{
    if (provider == DatabaseProvider.Sqlite)
    {
        // EnsureCreated cannot be used here: opening a SQLite file creates it, which makes EF think
        // the database already exists and skip table creation. Use explicit DDL matching the
        // Serilog SQLite sink's schema instead.
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
            """
        );
        return;
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using NpgsqlCommand command = connection.CreateCommand();
    command.CommandText = PostgresLogging.CreateTableSql;
    await command.ExecuteNonQueryAsync();
}

static async Task ResetPostgresSequencesAsync(string connectionString)
{
    string[] tablesWithIdentityId =
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

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    foreach (string table in tablesWithIdentityId)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT setval(pg_get_serial_sequence('\"{table}\"', 'Id'), "
            + $"COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"{table}\";";
        await command.ExecuteNonQueryAsync();
    }
}

static bool TryParseProvider(string value, out DatabaseProvider provider)
{
    switch (value.Trim().ToLowerInvariant())
    {
        case "sqlite":
            provider = DatabaseProvider.Sqlite;
            return true;
        case "postgres":
        case "postgresql":
        case "npgsql":
            provider = DatabaseProvider.Postgres;
            return true;
        default:
            provider = DatabaseProvider.Sqlite;
            return false;
    }
}

static Dictionary<string, string> ParseArgs(string[] input)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < input.Length; i++)
    {
        string arg = input[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        string trimmed = arg[2..];
        int equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex >= 0)
        {
            result[trimmed[..equalsIndex]] = trimmed[(equalsIndex + 1)..];
            continue;
        }

        if (i + 1 < input.Length && !input[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[trimmed] = input[i + 1];
            i++;
        }
        else
        {
            result[trimmed] = "true";
        }
    }

    return result;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- \\");
    Console.WriteLine("    --from <sqlite|postgres> --to <sqlite|postgres> \\");
    Console.WriteLine(
        "    --from-connection \"<connection string>\" --to-connection \"<connection string>\" \\"
    );
    Console.WriteLine("    [--batch-size 500] [--force] [--include-logs] \\");
    Console.WriteLine(
        "    [--from-logs-connection \"<sqlite logs connection>\"] [--to-logs-connection \"<sqlite logs connection>\"]"
    );
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine(
        "  - The target must be empty unless --force is passed (which clears it first)."
    );
    Console.WriteLine(
        "  - For SQLite, logs live in a separate file; pass the corresponding *-logs-connection."
    );
    Console.WriteLine(
        "  - Run the migration against a stopped application to avoid concurrent writes."
    );
}

internal sealed record TableOperation(
    string Name,
    Func<Task<bool>> HasData,
    Func<Task> Clear,
    Func<Task> Copy
);
