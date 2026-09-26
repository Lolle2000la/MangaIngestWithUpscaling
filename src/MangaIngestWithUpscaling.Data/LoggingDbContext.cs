using MangaIngestWithUpscaling.Data.LogModel;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Data;

public class LoggingDbContext(DbContextOptions<LoggingDbContext> options) : DbContext(options)
{
    protected DbSet<Log> LogsProtected { get; set; }
    public IQueryable<Log> Logs => LogsProtected.AsNoTracking();

    /// <summary>
    /// Mutable log set used by the data migration tool. Read paths should use <see cref="Logs"/>.
    /// </summary>
    public DbSet<Log> LogEntries => LogsProtected;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Log>(entity =>
        {
            entity.ToTable("Logs", t => t.ExcludeFromMigrations());
        });
    }
}
