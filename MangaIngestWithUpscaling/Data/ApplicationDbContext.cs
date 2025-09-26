using System.Text.Json;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MangaIngestWithUpscaling.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options),
        IDataProtectionKeyContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        AllowTrailingCommas = true,
    };

    public DbSet<Library> Libraries { get; set; }
    public DbSet<LibraryFilterRule> LibraryFilterRules { get; set; }
    public DbSet<LibraryRenameRule> LibraryRenameRules { get; set; }
    public DbSet<Manga> MangaSeries { get; set; }
    public DbSet<MangaAlternativeTitle> MangaAlternativeTitles { get; set; }
    public DbSet<Chapter> Chapters { get; set; }
    public DbSet<UpscalerProfile> UpscalerProfiles { get; set; }
    public DbSet<PersistedTask> PersistedTasks { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<MergedChapterInfo> MergedChapterInfos { get; set; }
    public DbSet<FilteredImage> FilteredImages { get; set; }

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    private void UpdateTimestamps()
    {
        var now = DateTime.UtcNow;
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            // Set ModifiedAt for all entities that have this property
            if (entry.Entity is Chapter chapter)
            {
                if (entry.State == EntityState.Added)
                    chapter.CreatedAt = now;
                chapter.ModifiedAt = now;
            }
            else if (entry.Entity is Manga manga)
            {
                if (entry.State == EntityState.Added)
                    manga.CreatedAt = now;
                manga.ModifiedAt = now;
            }
            else if (entry.Entity is Library library)
            {
                if (entry.State == EntityState.Added)
                    library.CreatedAt = now;
                library.ModifiedAt = now;
            }
            else if (entry.Entity is UpscalerProfile profile)
            {
                if (entry.State == EntityState.Added)
                    profile.CreatedAt = now;
                profile.ModifiedAt = now;
            }
            else if (entry.Entity is MangaAlternativeTitle alternativeTitle && entry.State == EntityState.Added)
            {
                alternativeTitle.CreatedAt = now;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Manga>(entity =>
        {
            entity.HasIndex(e => new { e.PrimaryTitle }).IsUnique();
        });

        builder.Entity<MangaAlternativeTitle>(entity =>
        {
            entity.HasKey(e => new { e.MangaId, e.Title });

            entity
                .HasOne(e => e.Manga)
                .WithMany(e => e.OtherTitles)
                .HasForeignKey(e => e.MangaId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.Title }).IsUnique();
        });

        builder.Entity<Chapter>(entity =>
        {
            entity.HasIndex(e => new { e.RelativePath, e.MangaId }).IsUnique();
        });

        builder.Entity<PersistedTask>(entity =>
        {
            entity
                .Property(e => e.Data)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<BaseTask>(v, JsonOptions)!
                )
                .HasColumnType("jsonb"); // Use 'json' for SQL Server

            entity.Property(e => e.Status).HasConversion<string>();

            entity.Property(e => e.Order).UseSequence();

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ProcessedAt);
        });

        builder.Entity<LibraryFilterRule>(entity =>
        {
            entity.Property(e => e.PatternType).HasConversion<string>();
            entity.Property(e => e.TargetField).HasConversion<string>();
            entity.Property(e => e.Action).HasConversion<string>();
        });
        builder.Entity<LibraryRenameRule>(entity =>
        {
            entity.Property(e => e.PatternType).HasConversion<string>();
            entity.Property(e => e.TargetField).HasConversion<string>();
        });

        builder.Entity<UpscalerProfile>(entity =>
        {
            entity.Property(e => e.UpscalerMethod).HasConversion<string>();
            entity.Property(e => e.ScalingFactor).HasConversion<string>();
            entity.Property(e => e.CompressionFormat).HasConversion<string>();
            entity.HasQueryFilter(e => !e.Deleted);
        });

        builder.Entity<MergedChapterInfo>(entity =>
        {
            var comparer = new ValueComparer<List<OriginalChapterPart>>(
                (a, b) =>
                    (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                a => a == null ? 0 : a.Aggregate(0, (h, v) => HashCode.Combine(h, v.GetHashCode())),
                a => a == null ? new List<OriginalChapterPart>() : a.ToList()
            );

            builder
                .Entity<MergedChapterInfo>()
                .Property(m => m.OriginalParts)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<OriginalChapterPart>>(v, JsonOptions)!
                )
                .HasColumnType("jsonb") // Use 'json' for SQL Server
                .Metadata.SetValueComparer(comparer);

            entity
                .HasOne(e => e.Chapter)
                .WithOne()
                .HasForeignKey<MergedChapterInfo>(e => e.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ChapterId).IsUnique();
        });

        builder.Entity<FilteredImage>(entity =>
        {
            entity
                .HasOne(e => e.Library)
                .WithMany(e => e.FilteredImages)
                .HasForeignKey(e => e.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.LibraryId);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.PerceptualHash);
            entity.HasIndex(e => e.DateAdded);
            entity.HasIndex(e => e.OccurrenceCount);
            entity.HasIndex(e => new { e.LibraryId, e.OriginalFileName });
        });
    }
}
