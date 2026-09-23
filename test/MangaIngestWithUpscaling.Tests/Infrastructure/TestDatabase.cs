using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>The relational backend a test database is created on.</summary>
public enum TestDatabaseBackend
{
    Sqlite,
    Postgres,
}

/// <summary>
/// Creates isolated test databases. The backend is selected with the <c>TEST_DB_PROVIDER</c>
/// environment variable (<c>sqlite</c>, the default, or <c>postgres</c>), so the same test suite can
/// be run against both providers. PostgreSQL uses a single shared Testcontainers instance and one
/// throwaway database per <see cref="TestDatabase"/>.
/// </summary>
public static class TestDatabaseFactory
{
    private static readonly Lazy<TestDatabaseBackend> CurrentBackend = new(ResolveBackend);
    private static readonly Lazy<Task<PostgreSqlContainer>> PostgresContainer = new(StartContainer);

    public static TestDatabaseBackend Backend => CurrentBackend.Value;

    public static TestDatabase Create() =>
        Backend switch
        {
            TestDatabaseBackend.Sqlite => TestDatabase.CreateSqlite(),
            TestDatabaseBackend.Postgres => TestDatabase
                .CreatePostgresAsync(PostgresContainer.Value.GetAwaiter().GetResult())
                .GetAwaiter()
                .GetResult(),
            _ => throw new ArgumentOutOfRangeException(),
        };

    private static TestDatabaseBackend ResolveBackend()
    {
        string? value = Environment.GetEnvironmentVariable("TEST_DB_PROVIDER");
        return
            string.Equals(value, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "postgresql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "npgsql", StringComparison.OrdinalIgnoreCase)
            ? TestDatabaseBackend.Postgres
            : TestDatabaseBackend.Sqlite;
    }

    private static async Task<PostgreSqlContainer> StartContainer()
    {
        var container = new PostgreSqlBuilder("postgres:17")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithDatabase("postgres")
            .WithCleanUp(true)
            .Build();

        await container.StartAsync();
        return container;
    }
}

/// <summary>
/// A single isolated test database. Multiple contexts can be created against it; the schema is
/// created once with <see cref="DbContext.Database"/>'s <c>EnsureCreated</c>.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ApplicationDbContext>> _createContext;
    private readonly Func<CancellationToken, Task> _dispose;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private TestDatabase(
        TestDatabaseBackend backend,
        string connectionString,
        Func<CancellationToken, Task<ApplicationDbContext>> createContext,
        Func<CancellationToken, Task> dispose
    )
    {
        Backend = backend;
        ConnectionString = connectionString;
        _createContext = createContext;
        _dispose = dispose;
    }

    public TestDatabaseBackend Backend { get; }

    public string ConnectionString { get; }

    public async Task<ApplicationDbContext> CreateContextAsync(
        CancellationToken cancellationToken = default
    )
    {
        ApplicationDbContext context = await _createContext(cancellationToken);
        await EnsureCreatedAsync(context, cancellationToken);
        return context;
    }

    public ApplicationDbContext CreateContext() =>
        CreateContextAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _dispose(CancellationToken.None);
        _initLock.Dispose();
    }

    private async Task EnsureCreatedAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken
    )
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await context.Database.EnsureCreatedAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    internal static TestDatabase CreateSqlite()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"file:test-{Guid.NewGuid():N}?mode=memory&cache=shared",
        }.ToString();

        var keeper = new SqliteConnection(connectionString);
        keeper.Open();

        async Task<ApplicationDbContext> CreateContext(CancellationToken token)
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(token);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(
                    connection,
                    sqlite =>
                    {
                        sqlite.MigrationsAssembly(
                            typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName
                        );
                        sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    }
                )
                .Options;

            return new ApplicationDbContext(options);
        }

        async Task Dispose(CancellationToken token)
        {
            await keeper.DisposeAsync();
        }

        return new TestDatabase(
            TestDatabaseBackend.Sqlite,
            connectionString,
            CreateContext,
            Dispose
        );
    }

    internal static async Task<TestDatabase> CreatePostgresAsync(PostgreSqlContainer container)
    {
        string baseConnectionString = container.GetConnectionString();
        string databaseName = $"tests_{Guid.NewGuid():N}";

        await using (var adminDataSource = NpgsqlDataSource.Create(baseConnectionString))
        await using (
            var adminConnection = await adminDataSource.OpenConnectionAsync(CancellationToken.None)
        )
        await using (var createDatabase = adminConnection.CreateCommand())
        {
            createDatabase.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await createDatabase.ExecuteNonQueryAsync(CancellationToken.None);
        }

        string connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        Task<ApplicationDbContext> CreateContext(CancellationToken token)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(
                    connectionString,
                    npgsql =>
                    {
                        npgsql.MigrationsAssembly(
                            typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName
                        );
                        npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    }
                )
                .Options;

            return Task.FromResult(new ApplicationDbContext(options));
        }

        async Task Dispose(CancellationToken token)
        {
            await using var adminDataSource = NpgsqlDataSource.Create(baseConnectionString);
            await using var adminConnection = await adminDataSource.OpenConnectionAsync(token);
            await using var dropDatabase = adminConnection.CreateCommand();
            dropDatabase.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await dropDatabase.ExecuteNonQueryAsync(token);
        }

        return new TestDatabase(
            TestDatabaseBackend.Postgres,
            connectionString,
            CreateContext,
            Dispose
        );
    }
}
