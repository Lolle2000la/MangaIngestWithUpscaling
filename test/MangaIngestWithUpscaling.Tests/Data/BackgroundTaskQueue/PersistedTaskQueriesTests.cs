using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Data.BackgroundTaskQueue;

/// <summary>
/// Exercises the provider-specific SQL over the persisted-task JSON payload on both SQLite and
/// PostgreSQL (selected via <c>TEST_DB_PROVIDER</c>).
/// </summary>
public class PersistedTaskQueriesTests : IDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly ApplicationDbContext _db;

    public PersistedTaskQueriesTests()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _db = _testDb.Context;
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task ForTaskTypeAndChapter_ReturnsOnlyMatchingTypeAndChapter()
    {
        await SeedAsync();

        List<PersistedTask> result = await PersistedTaskQueries
            .ForTaskTypeAndChapter<UpscaleTask>(_db, chapterId: 1)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, task => Assert.IsType<UpscaleTask>(task.Data));
    }

    [Fact]
    public async Task ForTaskTypeAndChapter_AppliesStatusFilter()
    {
        await SeedAsync();

        List<PersistedTask> result = await PersistedTaskQueries
            .ForTaskTypeAndChapter<UpscaleTask>(_db, chapterId: 1, [PersistedTaskStatus.Pending])
            .ToListAsync(TestContext.Current.CancellationToken);

        PersistedTask task = Assert.Single(result);
        Assert.Equal(PersistedTaskStatus.Pending, task.Status);
    }

    [Fact]
    public async Task ForTaskTypesAndChapters_MatchesMultipleTypesAndChapters()
    {
        await SeedAsync();

        List<PersistedTask> result = await PersistedTaskQueries
            .ForTaskTypesAndChapters(
                _db,
                chapterIds: [1, 2],
                taskTypes: [nameof(UpscaleTask), nameof(DetectSplitCandidatesTask)]
            )
            .ToListAsync(TestContext.Current.CancellationToken);

        // Two UpscaleTask rows for chapter 1 plus one DetectSplitCandidatesTask for chapter 2.
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ForTaskTypesAndChapters_AppliesStatusFilter()
    {
        await SeedAsync();

        List<PersistedTask> result = await PersistedTaskQueries
            .ForTaskTypesAndChapters(
                _db,
                chapterIds: [1, 2],
                taskTypes: [nameof(UpscaleTask), nameof(RepairUpscaleTask)],
                statuses: [PersistedTaskStatus.Pending, PersistedTaskStatus.Processing]
            )
            .ToListAsync(TestContext.Current.CancellationToken);

        // The completed UpscaleTask is excluded.
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ForTaskTypesAndChapters_ReturnsEmptyWhenNothingMatches()
    {
        await SeedAsync();

        List<PersistedTask> result = await PersistedTaskQueries
            .ForTaskTypesAndChapters(_db, chapterIds: [999], taskTypes: [nameof(UpscaleTask)])
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    private async Task SeedAsync()
    {
        _db.PersistedTasks.AddRange(
            new PersistedTask
            {
                Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
                Status = PersistedTaskStatus.Pending,
                Order = 1,
            },
            new PersistedTask
            {
                Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
                Status = PersistedTaskStatus.Completed,
                Order = 2,
            },
            new PersistedTask
            {
                Data = new RepairUpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
                Status = PersistedTaskStatus.Processing,
                Order = 3,
            },
            new PersistedTask
            {
                Data = new DetectSplitCandidatesTask(chapterId: 2, detectorVersion: 1),
                Status = PersistedTaskStatus.Pending,
                Order = 4,
            },
            new PersistedTask
            {
                Data = new ApplySplitsTask(chapterId: 2, detectorVersion: 1),
                Status = PersistedTaskStatus.Pending,
                Order = 5,
            }
        );

        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
