using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Data.LogModel;

/// <summary>
/// Minimal DbContext for querying Serilog's SQLite log store from the UI.
/// </summary>
public class LoggingDbContext : DbContext
{
    public LoggingDbContext(DbContextOptions<LoggingDbContext> options)
        : base(options) { }

    public DbSet<Log> Logs => Set<Log>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Log>().ToTable("Logs");
    }
}
