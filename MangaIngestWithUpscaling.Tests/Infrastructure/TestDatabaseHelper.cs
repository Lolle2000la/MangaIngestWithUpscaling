using MangaIngestWithUpscaling.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// Helper class for creating SQLite in-memory databases for testing
/// </summary>
public static class TestDatabaseHelper
{
    /// <summary>
    /// Creates a new SQLite in-memory database context for testing
    /// </summary>
    /// <returns>A disposable context that maintains connection automatically</returns>
    public static TestDbContext CreateInMemoryDatabase()
    {
        return new TestDbContext();
    }

    /// <summary>
    /// Creates a DbContextFactory for testing
    /// </summary>
    /// <returns>A factory that creates contexts using a shared in-memory database</returns>
    public static TestDbContextFactory CreateInMemoryDatabaseFactory()
    {
        return new TestDbContextFactory();
    }

    public class TestDbContext : IDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Context { get; }

        public TestDbContext()
        {
            // Create in-memory SQLite database
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .RelationalEventId
                            .AmbientTransactionWarning
                    )
                )
                .Options;

            Context = new ApplicationDbContext(options);

            // Create the database schema
            Context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Context?.Dispose();
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    public class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _options;

        public TestDbContextFactory()
        {
            // Create shared in-memory SQLite database
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .RelationalEventId
                            .AmbientTransactionWarning
                    )
                )
                .Options;

            // Create the database schema
            using var context = new ApplicationDbContext(_options);
            context.Database.EnsureCreated();
        }

        public ApplicationDbContext CreateDbContext()
        {
            return new ApplicationDbContext(_options);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateDbContext());
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}
