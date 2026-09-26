using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// Convenience wrapper around <see cref="TestDatabaseFactory"/> for tests that only need a single
/// context. The backend is selected through <c>TEST_DB_PROVIDER</c>.
/// </summary>
public static class TestDatabaseHelper
{
    /// <summary>
    /// Creates a fresh, isolated database for testing and returns an asynchronously disposable
    /// wrapper around a context connected to it. The backend follows <c>TEST_DB_PROVIDER</c> (SQLite
    /// or PostgreSQL), so this is deliberately not an "in-memory" helper.
    /// </summary>
    public static TestDbContext CreateDatabase()
    {
        return new TestDbContext(TestDatabaseFactory.Create());
    }

    /// <summary>
    /// Owns a context and its underlying <see cref="TestDatabase"/>. Disposal is asynchronous
    /// because the database teardown (and the PostgreSQL container) is asynchronous; owning test
    /// classes should implement <see cref="IAsyncDisposable"/> so xUnit awaits it.
    /// </summary>
    public class TestDbContext : IAsyncDisposable
    {
        private readonly TestDatabase _database;

        public ApplicationDbContext Context { get; }

        public TestDbContext(TestDatabase database)
        {
            _database = database;
            Context = database.CreateContext();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _database.DisposeAsync();
        }
    }
}
