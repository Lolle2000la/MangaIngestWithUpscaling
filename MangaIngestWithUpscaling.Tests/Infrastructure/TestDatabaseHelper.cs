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
}
