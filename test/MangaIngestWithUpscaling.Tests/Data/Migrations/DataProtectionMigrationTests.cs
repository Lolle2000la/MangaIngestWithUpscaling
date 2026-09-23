using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.DbMigrator;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Verifies that the DataProtection key ring survives a provider migration, so existing auth
/// cookies, API keys and antiforgery tokens keep working after switching between SQLite and
/// PostgreSQL. The application persists the key ring to <see cref="ApplicationDbContext"/>.
/// </summary>
[Trait("Category", "Integration")]
public class DataProtectionMigrationTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to run the DataProtection migration test (Docker required).";

    private const string ApplicationName = "manga-ingest-with-upscaling";
    private const string Purpose = "test-purpose";
    private const string Secret = "super-secret-value";

    [Fact]
    public async Task DataProtectionKeys_SurviveMigrationInBothDirections()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );
        await using TestDatabase sqliteTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );

        string protectedPayload;
        await using (ApplicationDbContext source = sqliteSource.CreateContext())
        {
            IDataProtector protector = CreateProtector(source);
            protectedPayload = protector.Protect(Secret);

            Assert.True(await source.DataProtectionKeys.AnyAsync(ct));
        }

        // SQLite -> PostgreSQL
        await MigrateAsync(
            DatabaseProvider.Sqlite,
            sqliteSource,
            DatabaseProvider.Postgres,
            postgresTarget,
            ct
        );
        await using (
            ApplicationDbContext postgres = await postgresTarget.CreateContextAsync(
                ensureSchema: false,
                ct
            )
        )
        {
            IDataProtector protector = CreateProtector(postgres);
            Assert.Equal(Secret, protector.Unprotect(protectedPayload));
        }

        // PostgreSQL -> SQLite
        await MigrateAsync(
            DatabaseProvider.Postgres,
            postgresTarget,
            DatabaseProvider.Sqlite,
            sqliteTarget,
            ct
        );
        await using (
            ApplicationDbContext roundTripped = await sqliteTarget.CreateContextAsync(
                ensureSchema: false,
                ct
            )
        )
        {
            IDataProtector protector = CreateProtector(roundTripped);
            Assert.Equal(Secret, protector.Unprotect(protectedPayload));
        }
    }

    private static IDataProtector CreateProtector(ApplicationDbContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services
            .AddDataProtection()
            .PersistKeysToDbContext<ApplicationDbContext>()
            .SetApplicationName(ApplicationName);

        return services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose);
    }

    private static Task MigrateAsync(
        DatabaseProvider from,
        TestDatabase source,
        DatabaseProvider to,
        TestDatabase target,
        CancellationToken ct
    ) =>
        DataMigrator.MigrateAsync(
            new MigratorOptions(
                from,
                source.ConnectionString,
                to,
                target.ConnectionString,
                BatchSize: 500,
                Force: false,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );
}
