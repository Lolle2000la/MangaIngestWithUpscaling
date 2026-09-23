using System.Net;
using MangaIngestWithUpscaling.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// Boots the real web host against a PostgreSQL container and verifies it starts, applies the
/// migrations and serves its health endpoint. This exercises the provider wiring in
/// <c>Program.cs</c> (DbContext registration, logging context, startup guards) that unit tests
/// cannot reach. Configuration is supplied through the <c>Ingest_</c>-prefixed environment variables
/// the application reads at startup.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresApplicationStartupTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to run the PostgreSQL startup test (Docker required).";

    [Fact]
    public async Task Application_StartsMigratesAndServesHealthCheck_OnPostgres()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase database = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        // Program.cs reads configuration before the host is built, so WebApplicationFactory's
        // ConfigureAppConfiguration would be applied too late; use the environment variables the app
        // already reads instead.
        var previous = new Dictionary<string, string?>
        {
            ["Ingest_DatabaseProvider"] = Environment.GetEnvironmentVariable(
                "Ingest_DatabaseProvider"
            ),
            ["Ingest_ConnectionStrings__PostgresConnection"] = Environment.GetEnvironmentVariable(
                "Ingest_ConnectionStrings__PostgresConnection"
            ),
            ["Ingest_Upscaler__RemoteOnly"] = Environment.GetEnvironmentVariable(
                "Ingest_Upscaler__RemoteOnly"
            ),
        };

        try
        {
            Environment.SetEnvironmentVariable("Ingest_DatabaseProvider", "Postgres");
            Environment.SetEnvironmentVariable(
                "Ingest_ConnectionStrings__PostgresConnection",
                database.ConnectionString
            );
            Environment.SetEnvironmentVariable("Ingest_Upscaler__RemoteOnly", "true");

            await using var factory = new WebApplicationFactory<Program>();
            using HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.GetAsync("/health", ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            foreach ((string key, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        // The application must have migrated the PostgreSQL database, not a SQLite fallback.
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
        Assert.Equal(0, await context.PersistedTasks.CountAsync(ct));
    }
}
