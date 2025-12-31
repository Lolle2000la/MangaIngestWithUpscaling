using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.BackgroundTaskQueue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

public enum TestDatabaseBackend
{
    Sqlite,
    Postgres,
}

public static class TestDatabaseBackends
{
    private static readonly Lazy<TheoryData<TestDatabaseBackend>> EnabledBackendsLazy = new(
        BuildEnabledBackends
    );

    public static TheoryData<TestDatabaseBackend> Enabled => EnabledBackendsLazy.Value;

    public static bool PostgresEnabled
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TEST_ENABLE_POSTGRES");
            if (string.IsNullOrWhiteSpace(env))
            {
                return true; // default: run Postgres-backed tests
            }

            return env.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
                || env.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                || env.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
                || env.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static TheoryData<TestDatabaseBackend> BuildEnabledBackends()
    {
        var data = new TheoryData<TestDatabaseBackend> { TestDatabaseBackend.Sqlite };
        if (PostgresEnabled)
        {
            data.Add(TestDatabaseBackend.Postgres);
        }

        return data;
    }
}

[CollectionDefinition(Name)]
public sealed class TestDatabaseCollection : ICollectionFixture<TestDatabaseFixture>
{
    public const string Name = "Database collection";
}

public sealed class TestDatabaseFixture : IAsyncLifetime, IAsyncDisposable
{
    private PostgreSqlContainer? _postgresContainer;

    public async ValueTask InitializeAsync()
    {
        TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);

        if (TestDatabaseBackends.PostgresEnabled)
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:17")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithDatabase("postgres")
                .WithCleanUp(true)
                .Build();

            await _postgresContainer.StartAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgresContainer is not null)
        {
            await _postgresContainer.DisposeAsync();
        }
    }

    public async Task<TestDatabase> CreateDatabaseAsync(
        TestDatabaseBackend backend,
        CancellationToken cancellationToken
    )
    {
        return backend switch
        {
            TestDatabaseBackend.Sqlite => await TestDatabase.CreateSqliteAsync(cancellationToken),
            TestDatabaseBackend.Postgres when _postgresContainer is not null =>
                await TestDatabase.CreatePostgresAsync(_postgresContainer, cancellationToken),
            TestDatabaseBackend.Postgres => throw new InvalidOperationException(
                "Postgres backend disabled. Set TEST_ENABLE_POSTGRES=1 to enable."
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "Unsupported backend"
            ),
        };
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsync();
    }
}

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
        var context = await _createContext(cancellationToken);
        await EnsureCreatedAsync(context, cancellationToken);
        return context;
    }

    public ApplicationDbContext CreateContext()
    {
        return CreateContextAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _dispose(CancellationToken.None);
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

    public static async Task<TestDatabase> CreateSqliteAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"file:test-{Guid.NewGuid():N}?mode=memory&cache=shared",
        }.ToString();

        var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync(cancellationToken);

        async Task<ApplicationDbContext> CreateContext(CancellationToken token)
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(token);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(
                    connection,
                    sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
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

    public static async Task<TestDatabase> CreatePostgresAsync(
        PostgreSqlContainer container,
        CancellationToken cancellationToken
    )
    {
        var baseConnectionString = container.GetConnectionString();
        var databaseName = $"tests_{Guid.NewGuid():N}";

        await using (var adminDataSource = NpgsqlDataSource.Create(baseConnectionString))
        await using (
            var adminConnection = await adminDataSource.OpenConnectionAsync(cancellationToken)
        )
        await using (var createDatabase = adminConnection.CreateCommand())
        {
            createDatabase.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await createDatabase.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        async Task<ApplicationDbContext> CreateContext(CancellationToken token)
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
                        npgsql.EnableRetryOnFailure();
                    }
                )
                .Options;

            return new ApplicationDbContext(options);
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
