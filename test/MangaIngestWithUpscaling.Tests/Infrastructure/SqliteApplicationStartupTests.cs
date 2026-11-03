using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// Serialises the startup tests: both set and restore process-wide <c>Ingest_</c> environment
/// variables, so they must not run concurrently with each other. Other collections still run in
/// parallel, so tests that read <c>Ingest_</c> configuration must join this collection — the rest
/// build their services explicitly and are unaffected.
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
        string dataDirectory = CreateDataDirectory();
        Dictionary<string, string?> previous = CaptureEnvironment();

        try
        {
            ConfigureSqliteEnvironment(dataDirectory);

            await using var factory = new WebApplicationFactory<Program>();
            using HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.GetAsync("/health", ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            RestoreEnvironment(previous);
            DeleteDataDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task ChapterImageEndpoint_RequiresAuthentication_WhenOidcIsDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dataDirectory = CreateDataDirectory();
        Dictionary<string, string?> previous = CaptureEnvironment();

        try
        {
            ConfigureSqliteEnvironment(dataDirectory);

            await using var factory = new WebApplicationFactory<Program>();
            using HttpClient client = factory.CreateClient();

            // OIDC is disabled by default, so the global fallback authorization policy is not
            // installed; this endpoint serves private library content and must still reject
            // anonymous callers. Without the [Authorize] attribute the request reaches the action
            // and returns the "chapter not found" 404 instead.
            HttpResponseMessage response = await client.GetAsync(
                "/api/images/chapter/1/file/page.jpg",
                ct
            );

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            RestoreEnvironment(previous);
            DeleteDataDirectory(dataDirectory);
        }
    }

    private static string CreateDataDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"startup-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Dictionary<string, string?> CaptureEnvironment() =>
        new()
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

    // Program.cs reads configuration before the host is built, so use the environment variables the
    // application already reads rather than WebApplicationFactory's delayed callbacks.
    private static void ConfigureSqliteEnvironment(string dataDirectory)
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
    }

    private static void RestoreEnvironment(Dictionary<string, string?> previous)
    {
        foreach ((string key, string? value) in previous)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static void DeleteDataDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
