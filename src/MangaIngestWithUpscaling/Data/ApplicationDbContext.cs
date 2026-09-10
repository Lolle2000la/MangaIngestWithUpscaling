using System.Text.Json;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.Abstractions;
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
    public DbSet<LibraryIngestPath> LibraryIngestPaths { get; set; }
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
    public DbSet<StripSplitFinding> StripSplitFindings { get; set; }
    public DbSet<ChapterSplitProcessingState> ChapterSplitProcessingStates { get; set; }

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
        var entries = ChangeTracker
            .Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            // Entities opt into automatic timestamps by implementing the interfaces.
            if (entry.State == EntityState.Added && entry.Entity is ICreatedAt created)
            {
                created.CreatedAt = now;
            }

            if (entry.Entity is IModifiedAt modified)
            {
                modified.ModifiedAt = now;
            }
        }

        UpdateLibraryTimestampForChangedIngestPaths(now);
    }

    /// <summary>
    /// Adding, removing or editing an ingest path changes the owning library, so bump its
    /// <see cref="Library.ModifiedAt"/>. This stays separate from <see cref="UpdateTimestamps"/>
    /// because it must also react to deleted entries, whereas entity timestamps are only updated for
    /// added or modified ones.
    /// </summary>
    private void UpdateLibraryTimestampForChangedIngestPaths(DateTime now)
    {
        // Index the persisted libraries for O(1) owner lookups. Unsaved libraries (Id == 0) are not
        // indexed because they would collide on that key and are matched via the navigation instead.
        Dictionary<int, Library> trackedLibrariesById = ChangeTracker
            .Entries<Library>()
            .Select(e => e.Entity)
            .Where(l => l.Id != 0)
            .ToDictionary(l => l.Id);

        foreach (var ingestPathEntry in ChangeTracker.Entries<LibraryIngestPath>())
        {
            bool pathChanged =
                ingestPathEntry.State
                is EntityState.Added
                    or EntityState.Modified
                    or EntityState.Deleted;
            if (!pathChanged)
            {
                continue;
            }

            Library? owner = ingestPathEntry.Entity.Library;
            if (
                owner is null
                && trackedLibrariesById.TryGetValue(
                    ingestPathEntry.Entity.LibraryId,
                    out Library? trackedLibrary
                )
            )
            {
                owner = trackedLibrary;
            }

            if (owner != null)
            {
                owner.ModifiedAt = now;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Manga>(entity =>
        {
            entity.HasIndex(e => new { e.PrimaryTitle }).IsUnique();
            entity.HasIndex(e => e.LibraryId);
            entity.HasIndex(e => e.UpscalerProfilePreferenceId);
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
            entity.HasIndex(e => e.IsUpscaled);
            entity.HasIndex(e => e.MangaId);
            entity.HasIndex(e => e.UpscalerProfileId);
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
            entity.HasIndex(e => e.Order);
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(t => new
            {
                t.Status,
                t.Order,
                t.Id,
            });
        });

        builder.Entity<LibraryIngestPath>(entity =>
        {
            entity
                .HasOne(e => e.Library)
                .WithMany(e => e.IngestPaths)
                .HasForeignKey(e => e.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Also covers lookups by LibraryId alone.
            entity.HasIndex(e => new { e.LibraryId, e.Path }).IsUnique();
        });

        builder.Entity<LibraryFilterRule>(entity =>
        {
            entity.Property(e => e.PatternType).HasConversion<string>();
            entity.Property(e => e.TargetField).HasConversion<string>();
            entity.Property(e => e.Action).HasConversion<string>();
            entity.HasIndex(e => e.LibraryId);
        });
        builder.Entity<LibraryRenameRule>(entity =>
        {
            entity.Property(e => e.PatternType).HasConversion<string>();
            entity.Property(e => e.TargetField).HasConversion<string>();
            entity.HasIndex(e => e.LibraryId);
        });

        builder.Entity<UpscalerProfile>(entity =>
        {
            entity.Property(e => e.UpscalerMethod).HasConversion<string>();
            entity.Property(e => e.ScalingFactor).HasConversion<string>();
            entity.Property(e => e.CompressionFormat).HasConversion<string>();
            entity.HasQueryFilter(e => !e.Deleted);
            entity.HasIndex(e => e.Deleted);
            entity.HasIndex(e => new { e.Id, e.Deleted });
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
            entity.HasIndex(e => e.MergedChapterNumber);
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

        builder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(e => e.Key).IsUnique();
        });
    }
}
