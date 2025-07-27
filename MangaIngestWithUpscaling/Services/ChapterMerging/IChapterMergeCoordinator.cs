using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

public interface IChapterMergeCoordinator
{
    /// <summary>
    ///     Performs retroactive chapter merging for chapters that should be merged
    /// </summary>
    Task CheckAndMergeRetroactiveChapterPartsAsync(Manga manga, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Updates database for retroactive merging operations
    /// </summary>
    Task UpdateDatabaseForRetroactiveMergeAsync(MergeInfo mergeInfo, List<Chapter> originalChapters,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Performs full chapter merging including upscaled version if needed
    /// </summary>
    Task MergeChaptersAsync(List<Chapter> chapters, Library library, CancellationToken cancellationToken = default);
}