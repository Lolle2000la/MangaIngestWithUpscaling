using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Owns the chapter-conflict rule: an <see cref="ApplySplitsTask" /> must not run while a
///     same-chapter upscale-family task is pending or in flight, and an upscale-family task must not
///     run while a same-chapter apply is <c>Processing</c>. The two queries are the symmetric halves
///     of one rule, so they live together here instead of being re-derived at each call site.
///
///     The gate serializes each processor's "check the conflict, then claim" sequence so the two
///     halves cannot interleave. The queries are stateless; only the gate is process-local instance
///     state, owned by the <see cref="TaskQueue" />.
/// </summary>
public sealed class ChapterConflictGuard
{
    // One gate per chapter. A slow leak (one SemaphoreSlim per chapter ever seen) is acceptable;
    // chapters are bounded by the library.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _chapterGates = new();

    /// <summary>
    ///     True when a same-chapter upscale-family task is pending or in flight.
    ///     <see cref="ApplySplitsTask" /> must not run concurrently with one: it rewrites the
    ///     original CBZ that a worker streams from.
    /// </summary>
    public static Task<bool> HasSameChapterUpscaleTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        CancellationToken cancellationToken
    ) =>
        AnyChapterTaskAsync(
            context,
            chapterId,
            TaskClassifier.UpscaleFamilyTaskTypeNames,
            [PersistedTaskStatus.Pending, PersistedTaskStatus.Processing],
            cancellationToken
        );

    /// <summary>
    ///     True when a same-chapter <see cref="ApplySplitsTask" /> is in flight. This is the
    ///     symmetric half of <see cref="HasSameChapterUpscaleTaskAsync" />: an upscale/repair/detect
    ///     cannot start while an apply is rewriting the same original (and upscaled) CBZ. Only a
    ///     <c>Processing</c> apply blocks: a merely <c>Pending</c> one is deferred by the guard above
    ///     whenever an upscale is pending, so treating it as a blocker here would deadlock the two.
    /// </summary>
    public static Task<bool> HasSameChapterApplyTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        CancellationToken cancellationToken
    ) =>
        AnyChapterTaskAsync(
            context,
            chapterId,
            [nameof(ApplySplitsTask)],
            [PersistedTaskStatus.Processing],
            cancellationToken
        );

    private static Task<bool> AnyChapterTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        IReadOnlyCollection<string> taskTypes,
        IReadOnlyCollection<PersistedTaskStatus> statuses,
        CancellationToken cancellationToken
    )
    {
        if (context.Database.IsRelational())
        {
            return PersistedTaskQueries
                .ForTaskTypesAndChapters(context, [chapterId], taskTypes, statuses)
                .AnyAsync(cancellationToken);
        }

        // The InMemory provider (unit tests only) cannot translate the JSON Data column, so filter the
        // status-matched rows in memory instead. Production always uses a relational provider.
        var wantedStatuses = statuses.ToHashSet();
        var wantedTypes = taskTypes.ToHashSet();
        return Task.FromResult(
            context
                .PersistedTasks.Where(t => wantedStatuses.Contains(t.Status))
                .AsEnumerable()
                .Any(t =>
                    wantedTypes.Contains(t.Data.GetType().Name)
                    && t.Data is IChapterTask chapterTask
                    && chapterTask.ChapterId == chapterId
                )
        );
    }

    /// <summary>
    ///     Serializes the "check the same-chapter conflict, then claim" sequence for one chapter.
    ///     The gate is held only for the check-and-claim, not for the task's run. See
    ///     <see cref="TaskQueue.AcquireChapterGateAsync" /> for the process-local caveat.
    /// </summary>
    public async Task<IDisposable> AcquireChapterGateAsync(
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        SemaphoreSlim gate = _chapterGates.GetOrAdd(chapterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new ChapterGateLease(gate);
    }

    private sealed class ChapterGateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
