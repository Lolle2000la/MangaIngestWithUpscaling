using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.DbMigrator;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Verifies the migrator preserves the value-converted columns that are easiest to corrupt:
/// a <c>ulong</c> perceptual hash (SQLite INTEGER ⇄ PostgreSQL numeric(20,0)), the merged-chapter
/// JSON payload, soft-deleted profiles (the migrator must bypass the global query filter) and
/// Unicode/null values.
/// </summary>
[Trait("Category", "Integration")]
public class DbMigratorDataFidelityTests
{
    private const string SkipReason =
        "Set TEST_DB_PROVIDER=postgres to run the DbMigrator fidelity tests (Docker required).";

    private const string UnicodeTitle = "漫画 Ünïcode ①";

    [Fact]
    public async Task Migrate_PreservesTrickyColumnTypesAndSoftDeletedRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );
        await using TestDatabase sqliteTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );

        await SeedAsync(sqliteSource, ct);

        await MigrateAsync(
            DatabaseProvider.Sqlite,
            sqliteSource,
            DatabaseProvider.Postgres,
            postgresTarget,
            force: false,
            ct
        );
        await AssertFidelityAsync(postgresTarget, ct);

        await MigrateAsync(
            DatabaseProvider.Postgres,
            postgresTarget,
            DatabaseProvider.Sqlite,
            sqliteTarget,
            force: false,
            ct
        );
        await AssertFidelityAsync(sqliteTarget, ct);
    }

    [Fact]
    public async Task Migrate_RefusesNonEmptyTargetUnlessForced()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDatabaseFactory.Backend != TestDatabaseBackend.Postgres, SkipReason);

        await using TestDatabase sqliteSource = TestDatabaseFactory.Create(
            TestDatabaseBackend.Sqlite
        );
        await using TestDatabase postgresTarget = TestDatabaseFactory.Create(
            TestDatabaseBackend.Postgres
        );

        await SeedAsync(sqliteSource, ct);
        await MigrateAsync(
            DatabaseProvider.Sqlite,
            sqliteSource,
            DatabaseProvider.Postgres,
            postgresTarget,
            force: false,
            ct
        );

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                MigrateAsync(
                    DatabaseProvider.Sqlite,
                    sqliteSource,
                    DatabaseProvider.Postgres,
                    postgresTarget,
                    force: false,
                    ct
                )
        );
        Assert.Contains("not empty", exception.Message);

        // --force clears the target and re-copies without duplicating.
        await MigrateAsync(
            DatabaseProvider.Sqlite,
            sqliteSource,
            DatabaseProvider.Postgres,
            postgresTarget,
            force: true,
            ct
        );

        await using ApplicationDbContext target = await postgresTarget.CreateContextAsync(
            ensureSchema: false,
            ct
        );
        Assert.Equal(1, await target.Libraries.CountAsync(ct));
        Assert.Equal(1, await target.FilteredImages.CountAsync(ct));
        Assert.Equal(2, await target.UpscalerProfiles.IgnoreQueryFilters().CountAsync(ct));
    }

    private static async Task SeedAsync(TestDatabase database, CancellationToken ct)
    {
        await using ApplicationDbContext context = database.CreateContext();

        var library = new Library
        {
            Name = "Fidelity Library",
            NotUpscaledLibraryPath = "/regular",
            UpscaledLibraryPath = "/upscaled",
            IngestPaths =
            {
                new LibraryIngestPath { Path = "/ingest", SortOrder = 0 },
            },
        };
        var manga = new Manga
        {
            PrimaryTitle = UnicodeTitle,
            Author = null,
            Library = library,
        };
        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = $"{UnicodeTitle}/chapter1.cbz",
            Manga = manga,
        };

        context.AddRange(library, manga, chapter);
        context.UpscalerProfiles.AddRange(
            new UpscalerProfile
            {
                Name = "Active profile",
                ScalingFactor = ScaleFactor.TwoX,
                CompressionFormat = CompressionFormat.Png,
                Quality = 90,
            },
            new UpscalerProfile
            {
                Name = "Deleted profile",
                ScalingFactor = ScaleFactor.OneX,
                CompressionFormat = CompressionFormat.Avif,
                Quality = 80,
                Deleted = true,
            }
        );
        await context.SaveChangesAsync(ct);

        context.FilteredImages.Add(
            new FilteredImage
            {
                Library = library,
                OriginalFileName = "page 😀.png",
                MimeType = "image/png",
                ContentHash = "abc123",
                ThumbnailBase64 = null,
                FileSizeBytes = 1234567890123,
                PerceptualHash = ulong.MaxValue,
            }
        );

        context.MergedChapterInfos.Add(
            new MergedChapterInfo
            {
                Chapter = chapter,
                MergedChapterNumber = "1",
                OriginalParts = new List<OriginalChapterPart>
                {
                    new()
                    {
                        FileName = "part1.cbz",
                        ChapterNumber = "1.1",
                        Metadata = new ExtractedMetadata(UnicodeTitle, "第１話", "1.1"),
                        OriginalComicInfoXml = "<ComicInfo><Series>漫画</Series></ComicInfo>",
                        PageNames = { "001.png", "002.png" },
                        StartPageIndex = 0,
                        EndPageIndex = 1,
                    },
                    new()
                    {
                        FileName = "part2.cbz",
                        ChapterNumber = "1.2",
                        Metadata = new ExtractedMetadata(UnicodeTitle, null, "1.2"),
                        PageNames = { "003.png" },
                        StartPageIndex = 2,
                        EndPageIndex = 2,
                    },
                },
            }
        );

        await context.SaveChangesAsync(ct);
    }

    private static async Task AssertFidelityAsync(TestDatabase database, CancellationToken ct)
    {
        await using ApplicationDbContext context = await database.CreateContextAsync(
            ensureSchema: false,
            ct
        );

        FilteredImage image = await context.FilteredImages.SingleAsync(ct);
        Assert.Equal(ulong.MaxValue, image.PerceptualHash);
        Assert.Equal("page 😀.png", image.OriginalFileName);
        Assert.Equal(1234567890123, image.FileSizeBytes);
        Assert.Null(image.ThumbnailBase64);

        MergedChapterInfo merged = await context.MergedChapterInfos.SingleAsync(ct);
        Assert.Equal(2, merged.OriginalParts.Count);
        OriginalChapterPart part = merged.OriginalParts[0];
        Assert.Equal("part1.cbz", part.FileName);
        Assert.Equal("1.1", part.ChapterNumber);
        Assert.Equal(UnicodeTitle, part.Metadata.Series);
        Assert.Equal("第１話", part.Metadata.ChapterTitle);
        Assert.Equal(new[] { "001.png", "002.png" }, part.PageNames);
        Assert.Equal(0, part.StartPageIndex);
        Assert.Equal(1, part.EndPageIndex);
        Assert.Equal("<ComicInfo><Series>漫画</Series></ComicInfo>", part.OriginalComicInfoXml);

        List<UpscalerProfile> profiles = await context
            .UpscalerProfiles.IgnoreQueryFilters()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.Deleted);
        Assert.Contains(profiles, p => !p.Deleted);

        Manga manga = await context.MangaSeries.SingleAsync(ct);
        Assert.Equal(UnicodeTitle, manga.PrimaryTitle);
        Assert.Null(manga.Author);
    }

    private static Task MigrateAsync(
        DatabaseProvider from,
        TestDatabase source,
        DatabaseProvider to,
        TestDatabase target,
        bool force,
        CancellationToken ct
    ) =>
        DataMigrator.MigrateAsync(
            new MigratorOptions(
                from,
                source.ConnectionString,
                to,
                target.ConnectionString,
                BatchSize: 500,
                Force: force,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            ),
            _ => { },
            ct
        );
}
