using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

/// <summary>
///     Single source of truth for how a task type is routed and guarded. The queue routes
///     upscale-family tasks to the upscale channel; the standard processor defers a same-chapter
///     apply while one is pending or in flight. Both used to spell the set out independently, so a
///     task type added to one list but not the other silently broke the routing or the guard.
/// </summary>
public static class TaskClassifier
{
    /// <summary>
    ///     The upscale-family task type names. These are routed to the upscale queue and, while
    ///     <c>Pending</c> or <c>Processing</c>, conflict with a same-chapter apply. The names are
    ///     the concrete type names because <see cref="PersistedTaskQueries" /> matches the JSON
    ///     <c>$type</c> discriminator on exactly that string.
    /// </summary>
    public static readonly IReadOnlyList<string> UpscaleFamilyTaskTypeNames =
    [
        nameof(UpscaleTask),
        nameof(RepairUpscaleTask),
        nameof(DetectSplitCandidatesTask),
        nameof(RenameUpscaledChaptersSeriesTask),
    ];

    /// <summary>
    ///     The chapter-scoped tasks the merge manager cancels or removes: the upscale family plus
    ///     <see cref="ApplySplitsTask" />, which runs on the standard processor but still touches a
    ///     chapter a merge is manipulating.
    /// </summary>
    public static readonly IReadOnlyList<string> ChapterScopedTaskTypeNames =
    [
        nameof(UpscaleTask),
        nameof(RepairUpscaleTask),
        nameof(RenameUpscaledChaptersSeriesTask),
        nameof(DetectSplitCandidatesTask),
        nameof(ApplySplitsTask),
    ];

    /// <summary>True when <paramref name="data" /> is routed to the upscale queue.</summary>
    public static bool IsUpscaleTask(BaseTask data) =>
        data
            is UpscaleTask
                or RenameUpscaledChaptersSeriesTask
                or RepairUpscaleTask
                or DetectSplitCandidatesTask;

    /// <summary>The chapter a task targets, or <c>null</c> when it is not chapter-scoped.</summary>
    public static int? GetChapterId(BaseTask data) =>
        data is IChapterTask chapterTask ? chapterTask.ChapterId : null;
}
