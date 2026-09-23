using MangaIngestWithUpscaling.Data.LogModel;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Data;

public class LoggingDbContext(DbContextOptions<LoggingDbContext> options) : DbContext(options)
{
    protected DbSet<Log> LogsProtected { get; set; }
    public IQueryable<Log> Logs => LogsProtected.AsNoTracking();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Log>(entity =>
        {
            entity.ToTable("Logs", t => t.ExcludeFromMigrations());
        });
    }
}
