using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

namespace MangaIngestWithUpscaling.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false, AllowTrailingCommas = true
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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Manga>(entity =>
        {
            entity.HasIndex(e => new { e.PrimaryTitle })
                .IsUnique();
        });

        builder.Entity<MangaAlternativeTitle>(entity =>
        {
            entity.HasKey(e => new { e.MangaId, e.Title });

            entity.HasOne(e => e.Manga)
                .WithMany(e => e.OtherTitles)
                .HasForeignKey(e => e.MangaId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.Title })
                .IsUnique();
        });

        builder.Entity<Chapter>(entity =>
        {
            entity.HasIndex(e => new { e.RelativePath, e.MangaId })
                .IsUnique();
        });

        builder.Entity<PersistedTask>(entity =>
        {
            entity.Property(e => e.Data)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<BaseTask>(v, JsonOptions)!)
                .HasColumnType("jsonb"); // Use 'json' for SQL Server

            entity.Property(e => e.Status)
                .HasConversion<string>();

            entity.Property(e => e.Order)
                .UseSequence();

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ProcessedAt);
        });

        builder.Entity<LibraryFilterRule>(entity =>
        {
            entity.Property(e => e.PatternType)
                .HasConversion<string>();
            entity.Property(e => e.TargetField)
                .HasConversion<string>();
            entity.Property(e => e.Action)
                .HasConversion<string>();
        });
        builder.Entity<LibraryRenameRule>(entity =>
        {
            entity.Property(e => e.PatternType).HasConversion<string>();
            entity.Property(e => e.TargetField).HasConversion<string>();
        });

        builder.Entity<UpscalerProfile>(entity =>
        {
            entity.Property(e => e.UpscalerMethod)
                .HasConversion<string>();
            entity.Property(e => e.ScalingFactor)
                .HasConversion<string>();
            entity.Property(e => e.CompressionFormat)
                .HasConversion<string>();
            entity.HasQueryFilter(e => !e.Deleted);
        });

        builder.Entity<MergedChapterInfo>(entity =>
        {
            var comparer = new ValueComparer<List<OriginalChapterPart>>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                a => a == null ? 0 : a.Aggregate(0, (h, v) => HashCode.Combine(h, v.GetHashCode())),
                a => a == null ? new List<OriginalChapterPart>() : a.ToList());

            builder.Entity<MergedChapterInfo>()
                .Property(m => m.OriginalParts)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<OriginalChapterPart>>(v, JsonOptions)!)
                .HasColumnType("jsonb") // Use 'json' for SQL Server
                .Metadata.SetValueComparer(comparer);

            entity.HasOne(e => e.Chapter)
                .WithOne()
                .HasForeignKey<MergedChapterInfo>(e => e.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ChapterId)
                .IsUnique();
        });

        builder.Entity<FilteredImage>(entity =>
        {
            entity.HasOne(e => e.Library)
                .WithMany(e => e.FilteredImages)
                .HasForeignKey(e => e.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.LibraryId);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.DateAdded);
            entity.HasIndex(e => e.OccurrenceCount);
            entity.HasIndex(e => new { e.LibraryId, e.OriginalFileName });
        });
    }
}