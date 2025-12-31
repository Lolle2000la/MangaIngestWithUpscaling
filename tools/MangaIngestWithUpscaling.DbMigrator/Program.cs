using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.BackgroundTaskQueue;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);

var options = ParseArgs(args);
if (!options.TryGetValue("sqlite-connection", out var sqliteConnection) ||
    !options.TryGetValue("postgres-connection", out var postgresConnection))
{
    PrintUsage();
    return 1;
}

int batchSize = options.TryGetValue("batch-size", out var batch) && int.TryParse(batch, out var parsed)
    ? Math.Max(parsed, 50)
    : 500;

Console.WriteLine($"Starting migration with batch size {batchSize}...");

var sqliteOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseSqlite(sqliteConnection, sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
    .Options;

var postgresOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(
        postgresConnection,
        npgsql =>
        {
            npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            npgsql.EnableRetryOnFailure();
        }
    )
    .Options;

await using var sqliteContext = new ApplicationDbContext(sqliteOptions);
await using var postgresContext = new ApplicationDbContext(postgresOptions);

Console.WriteLine("Applying migrations to Postgres...");
await postgresContext.Database.MigrateAsync();

postgresContext.ChangeTracker.AutoDetectChangesEnabled = false;

await EnsureTargetEmpty(postgresContext);

await CopyAsync(sqliteContext, postgresContext, c => c.Roles, postgresContext.Roles, "AspNetRoles", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.Users, postgresContext.Users, "AspNetUsers", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.RoleClaims, postgresContext.RoleClaims, "AspNetRoleClaims", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.UserClaims, postgresContext.UserClaims, "AspNetUserClaims", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.UserLogins, postgresContext.UserLogins, "AspNetUserLogins", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.UserRoles, postgresContext.UserRoles, "AspNetUserRoles", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.UserTokens, postgresContext.UserTokens, "AspNetUserTokens", batchSize);

await CopyAsync(sqliteContext, postgresContext, c => c.Libraries, postgresContext.Libraries, "Libraries", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.LibraryFilterRules, postgresContext.LibraryFilterRules, "LibraryFilterRules", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.LibraryRenameRules, postgresContext.LibraryRenameRules, "LibraryRenameRules", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.UpscalerProfiles, postgresContext.UpscalerProfiles, "UpscalerProfiles", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.MangaSeries, postgresContext.MangaSeries, "MangaSeries", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.MangaAlternativeTitles, postgresContext.MangaAlternativeTitles, "MangaAlternativeTitles", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.Chapters, postgresContext.Chapters, "Chapters", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.MergedChapterInfos, postgresContext.MergedChapterInfos, "MergedChapterInfos", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.FilteredImages, postgresContext.FilteredImages, "FilteredImages", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.ChapterSplitProcessingStates, postgresContext.ChapterSplitProcessingStates, "ChapterSplitProcessingStates", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.StripSplitFindings, postgresContext.StripSplitFindings, "StripSplitFindings", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.PersistedTasks, postgresContext.PersistedTasks, "PersistedTasks", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.ApiKeys, postgresContext.ApiKeys, "ApiKeys", batchSize);
await CopyAsync(sqliteContext, postgresContext, c => c.DataProtectionKeys, postgresContext.DataProtectionKeys, "DataProtectionKeys", batchSize);

Console.WriteLine("Migration completed successfully.");
return 0;

static async Task CopyAsync<TEntity>(
    ApplicationDbContext source,
    ApplicationDbContext target,
    Func<ApplicationDbContext, IQueryable<TEntity>> sourceQuery,
    DbSet<TEntity> targetSet,
    string name,
    int batchSize
)
    where TEntity : class
{
    if (await targetSet.AnyAsync())
    {
        throw new InvalidOperationException($"Target table '{name}' is not empty. Please start with a clean Postgres database.");
    }

    var total = await sourceQuery(source).CountAsync();
    if (total == 0)
    {
        Console.WriteLine($"{name}: no rows to migrate.");
        return;
    }

    Console.WriteLine($"{name}: migrating {total} rows...");

    var copied = 0;
    while (true)
    {
        var batch = await sourceQuery(source)
            .AsNoTracking()
            .Skip(copied)
            .Take(batchSize)
            .ToListAsync();

        if (batch.Count == 0)
        {
            break;
        }

        await targetSet.AddRangeAsync(batch);
        await target.SaveChangesAsync();
        target.ChangeTracker.Clear();

        copied += batch.Count;
        Console.WriteLine($"{name}: {copied}/{total} rows migrated...");
    }

    Console.WriteLine($"{name}: completed.");
}

static async Task EnsureTargetEmpty(ApplicationDbContext target)
{
    var checks = new List<(Func<Task<bool>> hasData, string name)>
    {
        (() => target.Roles.AnyAsync(), "AspNetRoles"),
        (() => target.Users.AnyAsync(), "AspNetUsers"),
        (() => target.RoleClaims.AnyAsync(), "AspNetRoleClaims"),
        (() => target.UserClaims.AnyAsync(), "AspNetUserClaims"),
        (() => target.UserLogins.AnyAsync(), "AspNetUserLogins"),
        (() => target.UserRoles.AnyAsync(), "AspNetUserRoles"),
        (() => target.UserTokens.AnyAsync(), "AspNetUserTokens"),
        (() => target.Libraries.AnyAsync(), "Libraries"),
        (() => target.LibraryFilterRules.AnyAsync(), "LibraryFilterRules"),
        (() => target.LibraryRenameRules.AnyAsync(), "LibraryRenameRules"),
        (() => target.UpscalerProfiles.AnyAsync(), "UpscalerProfiles"),
        (() => target.MangaSeries.AnyAsync(), "MangaSeries"),
        (() => target.MangaAlternativeTitles.AnyAsync(), "MangaAlternativeTitles"),
        (() => target.Chapters.AnyAsync(), "Chapters"),
        (() => target.MergedChapterInfos.AnyAsync(), "MergedChapterInfos"),
        (() => target.FilteredImages.AnyAsync(), "FilteredImages"),
        (() => target.ChapterSplitProcessingStates.AnyAsync(), "ChapterSplitProcessingStates"),
        (() => target.StripSplitFindings.AnyAsync(), "StripSplitFindings"),
        (() => target.PersistedTasks.AnyAsync(), "PersistedTasks"),
        (() => target.ApiKeys.AnyAsync(), "ApiKeys"),
        (() => target.DataProtectionKeys.AnyAsync(), "DataProtectionKeys"),
    };

    foreach (var (hasData, name) in checks)
    {
        if (await hasData())
        {
            throw new InvalidOperationException($"Target database table '{name}' already has data. Use a fresh database for migration.");
        }
    }
}

static Dictionary<string, string> ParseArgs(string[] input)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < input.Length; i++)
    {
        var arg = input[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var trimmed = arg.Substring(2);
        var eqIndex = trimmed.IndexOf('=');
        if (eqIndex >= 0)
        {
            var key = trimmed[..eqIndex];
            var value = trimmed[(eqIndex + 1)..];
            result[key] = value;
        }
        else
        {
            var key = trimmed;
            if (i + 1 < input.Length && !input[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = input[i + 1];
                i++;
            }
            else
            {
                result[key] = "true";
            }
        }
    }

    return result;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- --sqlite-connection \"Data Source=./data.db\" --postgres-connection \"Host=localhost;Port=5432;Database=manga_ingest;Username=postgres;Password=postgres\" [--batch-size 500]");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("  - The Postgres database must be empty; this tool will stop if any rows already exist.");
    Console.WriteLine("  - Ensure both databases are accessible from the environment (container or local).");
}
