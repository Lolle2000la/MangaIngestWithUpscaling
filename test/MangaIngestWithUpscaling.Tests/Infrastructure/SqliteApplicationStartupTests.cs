using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// Serialises the startup tests: both set and restore process-wide <c>Ingest_</c> environment
/// variables, so they must not run concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApplicationStartupCollection
{
    public const string Name = "Application startup";
}

/// <summary>
/// Boots the real web host against SQLite and verifies it starts and serves its health endpoint.
/// This is the symmetric, Docker-free counterpart to <see cref="PostgresApplicationStartupTests"/>
/// and guards the default startup path (migrations, WAL, the .NET 10 upgrade marker) on CI for every
/// run, not just the PostgreSQL pass.
/// </summary>
[Collection(ApplicationStartupCollection.Name)]
public class SqliteApplicationStartupTests
{
    [Fact]
    public async Task Application_StartsAndServesHealthCheck_OnSqlite()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"startup-sqlite-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(dataDirectory);

        // Program.cs reads configuration before the host is built, so use the environment variables
        // the application already reads rather than WebApplicationFactory's delayed callbacks.
        var previous = new Dictionary<string, string?>
        {
            ["Ingest_DatabaseProvider"] = Environment.GetEnvironmentVariable(
                "Ingest_DatabaseProvider"
            ),
            ["Ingest_ConnectionStrings__DefaultConnection"] = Environment.GetEnvironmentVariable(
                "Ingest_ConnectionStrings__DefaultConnection"
            ),
            ["Ingest_ConnectionStrings__LoggingConnection"] = Environment.GetEnvironmentVariable(
                "Ingest_ConnectionStrings__LoggingConnection"
            ),
            ["Ingest_Upscaler__RemoteOnly"] = Environment.GetEnvironmentVariable(
                "Ingest_Upscaler__RemoteOnly"
            ),
        };

        try
        {
            Environment.SetEnvironmentVariable("Ingest_DatabaseProvider", "Sqlite");
            Environment.SetEnvironmentVariable(
                "Ingest_ConnectionStrings__DefaultConnection",
                $"Data Source={Path.Combine(dataDirectory, "data.db")};Mode=ReadWriteCreate"
            );
            Environment.SetEnvironmentVariable(
                "Ingest_ConnectionStrings__LoggingConnection",
                $"Data Source={Path.Combine(dataDirectory, "logs.db")}"
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

            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
