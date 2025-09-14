using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Services.MangaManagement;

[RegisterScoped]
public class MangaMerger(
    ApplicationDbContext dbContext,
    IMetadataHandlingService metadataHandling,
    IMangaMetadataChanger metadataChanger,
    ILogger<MangaMerger> logger,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier) : IMangaMerger
{
    /// <inheritdoc/>
    public async Task MergeAsync(Manga primary, IEnumerable<Manga> mergedInto,
        CancellationToken cancellationToken = default)
    {
        if (!dbContext.Entry(primary).Reference(m => m.Library).IsLoaded)
            await dbContext.Entry(primary).Reference(m => m.Library).LoadAsync(cancellationToken);
        if (!dbContext.Entry(primary).Collection(m => m.OtherTitles).IsLoaded)
        {
            await dbContext.Entry(primary).Collection(m => m.OtherTitles).LoadAsync(cancellationToken);
        }

        if (primary.Library == null)
        {
            logger.LogError(
                "Primary manga {MangaId} (Title: {PrimaryTitle}) must have an associated library to be a merge target. Aborting merge.",
                primary.Id, primary.PrimaryTitle);
            return;
        }

        // Step 1: Check if all files can be moved and collect the operation plan
        var mergeOperations = new List<MergeOperation>();
        var mangasToRemove = new List<Manga>();

        foreach (var manga in mergedInto)
        {
            if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
                await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
            if (!dbContext.Entry(manga).Collection(m => m.Chapters).IsLoaded)
                await dbContext.Entry(manga).Collection(m => m.Chapters).LoadAsync(cancellationToken);
            if (!dbContext.Entry(manga).Collection(m => m.OtherTitles).IsLoaded)
                await dbContext.Entry(manga).Collection(m => m.OtherTitles).LoadAsync(cancellationToken);

            var chaptersToMove = new List<ChapterMoveOperation>();
            var canMoveAllChapters = true;

            foreach (var chapter in manga.Chapters)
            {
                if (manga.Library == null)
                {
                    logger.LogWarning(
                        "Manga {MangaId} does not have a library associated. Skipping chapter {ChapterFileName}.",
                        manga.Id, chapter.FileName);
                    canMoveAllChapters = false;
                    continue;
                }

                var chapterPath = Path.Combine(manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
                if (!File.Exists(chapterPath))
                {
                    logger.LogWarning(
                        "Chapter {fileName} does not exist in {LibraryId} for {MangaId}. Therefore skipping.",
                        chapter.FileName, manga.LibraryId, manga.Id);
                    canMoveAllChapters = false;
                    continue;
                }

                var targetPath = Path.Combine(
                    primary.Library!.NotUpscaledLibraryPath,
                    PathEscaper.EscapeFileName(primary.PrimaryTitle!),
                    PathEscaper.EscapeFileName(chapter.FileName));

                if (File.Exists(targetPath))
                {
                    logger.LogWarning("Chapter {fileName} already exists in the target path {targetPath}. Skipping.",
                        chapter.FileName, targetPath);
                    canMoveAllChapters = false;
                    continue;
                }

                // Check if we can read existing metadata
                ExtractedMetadata? existingMetadata = null;
                try
                {
                    existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(chapterPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to read metadata from {chapterPath}. Skipping.",
                        chapterPath);
                    canMoveAllChapters = false;
                    continue;
                }

                string? upscaledSourcePath = null;
                if (chapter.IsUpscaled && manga.Library?.UpscaledLibraryPath != null &&
                    !string.IsNullOrEmpty(manga.Library.UpscaledLibraryPath))
                {
                    upscaledSourcePath = Path.Combine(manga.Library.UpscaledLibraryPath, chapter.RelativePath);
                    if (!File.Exists(upscaledSourcePath))
                    {
                        upscaledSourcePath = null; // File doesn't exist, skip upscaled move
                    }
                }

                chaptersToMove.Add(new ChapterMoveOperation
                {
                    Chapter = chapter,
                    SourcePath = chapterPath,
                    TargetPath = targetPath,
                    UpscaledSourcePath = upscaledSourcePath,
                    ExistingMetadata = existingMetadata,
                    NewRelativePath = Path.GetRelativePath(primary.Library!.NotUpscaledLibraryPath, targetPath)
                });
            }

            if (canMoveAllChapters && chaptersToMove.Count > 0)
            {
                mergeOperations.Add(new MergeOperation
                {
                    SourceManga = manga,
                    ChapterMoves = chaptersToMove,
                    TitlesToTransfer = GetTitlesToTransfer(manga, primary)
                });

                if (chaptersToMove.Count == manga.Chapters.Count)
                {
                    mangasToRemove.Add(manga);
                }
            }
            else if (chaptersToMove.Count == 0)
            {
                // No chapters to move, but we can still transfer titles if manga will be removed
                mangasToRemove.Add(manga);
                mergeOperations.Add(new MergeOperation
                {
                    SourceManga = manga, ChapterMoves = [], TitlesToTransfer = GetTitlesToTransfer(manga, primary)
                });
            }
            else
            {
                logger.LogWarning(
                    "Cannot move all chapters for manga {MangaId} ({PrimaryTitle}). Manga will not be merged.",
                    manga.Id, manga.PrimaryTitle);
            }
        }

        if (mergeOperations.Count == 0)
        {
            logger.LogWarning("No merge operations can be performed. Aborting merge.");
            return;
        }

        // Step 2: Perform database operations in a transaction
        using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Update chapter associations in the database
            foreach (var operation in mergeOperations)
            {
                foreach (var chapterMove in operation.ChapterMoves)
                {
                    chapterMove.Chapter.RelativePath = chapterMove.NewRelativePath;
                    chapterMove.Chapter.Manga = primary;
                    chapterMove.Chapter.MangaId = primary.Id;
                    primary.Chapters.Add(chapterMove.Chapter);
                    operation.SourceManga.Chapters.Remove(chapterMove.Chapter);
                }
            }

            // Remove mangas that have no chapters left and handle alternative titles
            foreach (var manga in mangasToRemove)
            {
                var operation = mergeOperations.First(o => o.SourceManga == manga);

                dbContext.MangaSeries.Remove(manga);

                // Transfer alternative titles using EF Core's recommended approach
                // Phase 1: Remove all existing alternative titles from source
                foreach (var title in manga.OtherTitles.ToList())
                {
                    manga.OtherTitles.Remove(title);
                }

                // Save changes to delete the old entities first
                await dbContext.SaveChangesAsync(cancellationToken);

                // Phase 2: Create new alternative title entities with correct principal
                foreach (var titleText in operation.TitlesToTransfer)
                {
                    // Avoid duplicates across multiple merged sources in the same transaction
                    if (!primary.OtherTitles.Any(t => string.Equals(t.Title, titleText, StringComparison.Ordinal)))
                    {
                        primary.OtherTitles.Add(new MangaAlternativeTitle
                        {
                            Title = titleText, Manga = primary, MangaId = primary.Id
                        });
                    }
                }
            }

            // Save all database changes
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Database transaction failed during manga merge. Rolling back.");
            throw;
        }

        // Step 3: If database transaction succeeded, perform file operations and metadata updates
        foreach (var operation in mergeOperations)
        {
            foreach (var chapterMove in operation.ChapterMoves)
            {
                try
                {
                    // Create target directory if it doesn't exist
                    var targetDir = Path.GetDirectoryName(chapterMove.TargetPath);
                    if (targetDir != null && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    // Update metadata in the source file before moving
                    metadataHandling.WriteComicInfo(chapterMove.SourcePath,
                        chapterMove.ExistingMetadata with { Series = primary.PrimaryTitle });

                    // Move the main chapter file
                    fileSystem.Move(chapterMove.SourcePath, chapterMove.TargetPath);
                    _ = chapterChangedNotifier.Notify(chapterMove.Chapter, false);

                    // Handle upscaled file if it exists
                    if (chapterMove.UpscaledSourcePath != null && primary.Library?.UpscaledLibraryPath != null)
                    {
                        try
                        {
                            metadataChanger.ApplyMangaTitleToUpscaled(chapterMove.Chapter, primary.PrimaryTitle!,
                                chapterMove.UpscaledSourcePath);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Failed to update metadata of upscaled chapter {fileName} in {MangaId}.",
                                chapterMove.Chapter.FileName, operation.SourceManga.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to move chapter {fileName} from {sourcePath} to {targetPath}. Database changes have been committed.",
                        chapterMove.Chapter.FileName, chapterMove.SourcePath, chapterMove.TargetPath);
                }
            }

            // Clean up empty source directories
            if (operation.SourceManga.Library != null)
            {
                string notUpscaledMangaDir = Path.Combine(operation.SourceManga.Library.NotUpscaledLibraryPath,
                    operation.SourceManga.PrimaryTitle);
                if (!FileSystemHelpers.DeleteIfEmpty(notUpscaledMangaDir, logger))
                {
                    logger.LogWarning(
                        "Manga {MangaId} was removed but the folder {notUpscaledMangaDir} was not empty.",
                        operation.SourceManga.Id, notUpscaledMangaDir);
                }

                if (operation.SourceManga.Library.UpscaledLibraryPath != null)
                {
                    string upscaledMangaDir = Path.Combine(operation.SourceManga.Library.UpscaledLibraryPath,
                        operation.SourceManga.PrimaryTitle);
                    FileSystemHelpers.DeleteIfEmpty(upscaledMangaDir, logger);
                }
            }
        }

        // Clean up empty subdirectories in all affected library paths
        _ = Task.Run(() =>
        {
            foreach (var uniqueLibraryPath in mergedInto
                         .SelectMany(m =>
                             new[] { m.Library?.NotUpscaledLibraryPath, m.Library?.UpscaledLibraryPath })
                         .Where(path => path is not null)
                         .Distinct())
            {
                FileSystemHelpers.DeleteEmptySubfolders(uniqueLibraryPath!, logger);
            }
        });
    }

    private List<string> GetTitlesToTransfer(Manga sourceManga, Manga targetManga)
    {
        // Use a HashSet to avoid dupes
        var titlesToTransfer = new HashSet<string>(StringComparer.Ordinal);

        static bool ContainsTitle(IEnumerable<MangaAlternativeTitle> titles, string title)
        {
            return titles.Any(t => string.Equals(t.Title, title, StringComparison.Ordinal));
        }

        // Add source primary title if it isn't the same as target primary title and not already present in target's other titles
        if (!string.IsNullOrWhiteSpace(sourceManga.PrimaryTitle)
            && !string.Equals(sourceManga.PrimaryTitle, targetManga.PrimaryTitle, StringComparison.Ordinal)
            && !ContainsTitle(targetManga.OtherTitles, sourceManga.PrimaryTitle))
        {
            titlesToTransfer.Add(sourceManga.PrimaryTitle);
        }

        // Collect other titles that should be transferred (not already in target, globally unique)
        foreach (var title in sourceManga.OtherTitles)
        {
            if (!string.IsNullOrWhiteSpace(title.Title)
                && !string.Equals(title.Title, targetManga.PrimaryTitle, StringComparison.Ordinal)
                && !ContainsTitle(targetManga.OtherTitles, title.Title))
            {
                titlesToTransfer.Add(title.Title);
            }
        }

        return titlesToTransfer.ToList();
    }

    private class MergeOperation
    {
        public required Manga SourceManga { get; set; }
        public required List<ChapterMoveOperation> ChapterMoves { get; set; }
        public required List<string> TitlesToTransfer { get; set; }
    }

    private class ChapterMoveOperation
    {
        public required Chapter Chapter { get; set; }
        public required string SourcePath { get; set; }
        public required string TargetPath { get; set; }
        public required string? UpscaledSourcePath { get; set; }
        public required ExtractedMetadata ExistingMetadata { get; set; }
        public required string NewRelativePath { get; set; }
    }
}