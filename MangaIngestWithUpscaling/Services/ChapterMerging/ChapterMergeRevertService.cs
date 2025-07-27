using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeRevertService(
    ApplicationDbContext dbContext,
    IChapterPartMerger chapterPartMerger,
    IChapterChangedNotifier chapterChangedNotifier,
    ILogger<ChapterMergeRevertService> logger) : IChapterMergeRevertService
{
    public async Task<List<Chapter>> RevertMergedChapterAsync(Chapter chapter,
        CancellationToken cancellationToken = default)
    {
        MergedChapterInfo? mergeInfo = await GetMergeInfoAsync(chapter, cancellationToken);
        if (mergeInfo == null)
        {
            throw new InvalidOperationException(
                $"Chapter {chapter.FileName} is not a merged chapter and cannot be reverted.");
        }

        // Load chapter dependencies
        if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
        {
            await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(chapter.Manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext.Entry(chapter.Manga).Reference(m => m.Library).LoadAsync(cancellationToken);
        }

        Library library = chapter.Manga.Library;
        string mergedChapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);

        if (!File.Exists(mergedChapterPath))
        {
            throw new FileNotFoundException($"Merged chapter file not found: {mergedChapterPath}");
        }

        try
        {
            // Get original parts information from strongly typed property
            List<OriginalChapterPart>? originalParts = mergeInfo.OriginalParts;
            if (originalParts == null || !originalParts.Any())
            {
                throw new InvalidOperationException("Invalid merge information: no original parts found.");
            }

            logger.LogInformation("Reverting merged chapter {ChapterFile} to {PartCount} original parts",
                chapter.FileName, originalParts.Count);

            // Create output directory for the restored parts
            string seriesDirectory = Path.Combine(library.NotUpscaledLibraryPath,
                PathEscaper.EscapeFileName(chapter.Manga.PrimaryTitle!));

            // Restore the original parts
            List<FoundChapter> restoredChapters = await chapterPartMerger.RestoreChapterPartsAsync(
                mergedChapterPath, originalParts, seriesDirectory, cancellationToken);

            // Create Chapter entities for the restored parts
            var restoredChapterEntities = new List<Chapter>();

            foreach (FoundChapter restoredChapter in restoredChapters)
            {
                var chapterEntity = new Chapter
                {
                    FileName = restoredChapter.FileName,
                    Manga = chapter.Manga,
                    MangaId = chapter.MangaId,
                    RelativePath = Path.GetRelativePath(library.NotUpscaledLibraryPath,
                        Path.Combine(seriesDirectory, restoredChapter.FileName)),
                    IsUpscaled = false,
                    UpscalerProfileId = chapter.UpscalerProfileId,
                    UpscalerProfile = chapter.UpscalerProfile
                };

                dbContext.Chapters.Add(chapterEntity);
                restoredChapterEntities.Add(chapterEntity);

                // Notify about the new chapter
                _ = chapterChangedNotifier.Notify(chapterEntity, false);
            }

            // Remove the merged chapter and its merge info
            dbContext.MergedChapterInfos.Remove(mergeInfo);
            dbContext.Chapters.Remove(chapter);

            // Delete the merged chapter file
            File.Delete(mergedChapterPath);

            // Clean up empty directories
            FileSystemHelpers.DeleteIfEmpty(Path.GetDirectoryName(mergedChapterPath)!, logger);

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Successfully reverted merged chapter {ChapterFile} to {PartCount} original parts",
                chapter.FileName, originalParts.Count);

            return restoredChapterEntities;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revert merged chapter {ChapterFile}", chapter.FileName);
            throw;
        }
    }

    public async Task<bool> CanRevertChapterAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        MergedChapterInfo? mergeInfo = await GetMergeInfoAsync(chapter, cancellationToken);
        return mergeInfo != null;
    }

    public async Task<MergedChapterInfo?> GetMergeInfoAsync(Chapter chapter,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.MergedChapterInfos
            .FirstOrDefaultAsync(m => m.ChapterId == chapter.Id, cancellationToken);
    }
}