using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MudBlazor;
using System.Xml;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

[RegisterScoped]
public class MangaMetadataChanger(
    IMetadataHandlingService metadataHandling,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IDialogService dialogService,
    ILogger<MangaMetadataChanger> logger,
    ITaskQueue taskQueue,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier) : IMangaMetadataChanger
{
    /// <inheritdoc/>
    public void ApplyMangaTitleToUpscaled(Chapter chapter, string newTitle, string origChapterPath)
    {
        if (!fileSystem.FileExists(origChapterPath))
        {
            throw new InvalidOperationException("Chapter file not found.");
        }

        if (chapter.Manga == null || chapter.Manga.Library == null)
        {
            throw new ArgumentNullException(
                "Chapter manga or library not found. Please ensure you have loaded it with the chapter.");
        }

        if (chapter.Manga.Library.UpscaledLibraryPath == null)
        {
            throw new InvalidOperationException("Upscaled library path not set.");
        }

        UpdateChapterTitle(newTitle, origChapterPath);

        // Move chapter to the correct directory with the new title
        var newChapterPath = Path.Combine(
            chapter.Manga.Library.UpscaledLibraryPath,
            PathEscaper.EscapeFileName(newTitle),
            PathEscaper.EscapeFileName(chapter.FileName));

        if (fileSystem.FileExists(newChapterPath))
        {
            logger.LogWarning("Chapter file already exists: {ChapterPath}", newChapterPath);
            return;
        }

        fileSystem.CreateDirectory(Path.GetDirectoryName(newChapterPath)!);
        fileSystem.Move(origChapterPath, newChapterPath);
        FileSystemHelpers.DeleteIfEmpty(Path.GetDirectoryName(origChapterPath)!, logger);
        _ = chapterChangedNotifier.Notify(chapter, true);
    }

    /// <inheritdoc/>
    public async Task<RenameResult> ChangeMangaTitle(Manga manga, string newTitle, bool addOldToAlternative = true,
        CancellationToken cancellationToken = default)
    {
        using var dbContext = dbContextFactory.CreateDbContext();
        
        var possibleCurrent = await dbContext.MangaSeries.FirstOrDefaultAsync(m =>
                m.Id != manga.Id && // prevent accidental self-merge
                (m.PrimaryTitle == newTitle || m.OtherTitles.Any(t => t.Title == newTitle)),
            cancellationToken: cancellationToken);

        if (possibleCurrent != null)
        {
            bool? consentToMerge = await dialogService.ShowMessageBox("Merge into existing manga of same name?",
                "The title you are trying to rename to already has an existing entry. " +
                "Do you want to merge this manga into the existing one?",
                yesText: "Merge", cancelText: "Cancel");
            if (consentToMerge == true)
            {
                await taskQueue.EnqueueAsync(new MergeMangaTask(possibleCurrent, [manga]));
                return RenameResult.Merged;
            }

            return RenameResult.Cancelled;
        }

        // Load the manga with all required data from the new context
        var managedManga = await dbContext.MangaSeries
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .Include(m => m.OtherTitles)
            .FirstOrDefaultAsync(m => m.Id == manga.Id, cancellationToken);

        if (managedManga?.Library == null)
        {
            logger.LogError(
                "Manga {MangaId} (Title: {PrimaryTitle}) must have an associated library to be renamed. Aborting rename.",
                manga.Id, manga.PrimaryTitle);
            return RenameResult.Cancelled;
        }

        // Step 1: Pre-flight validation - check if all files can be processed and collect operation plan
        var renameOperations = new List<ChapterRenameOperation>();
        var canRenameAllChapters = true;

        foreach (var chapter in managedManga.Chapters)
        {
            var currentChapterPath = Path.Combine(managedManga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
            if (!fileSystem.FileExists(currentChapterPath))
            {
                logger.LogWarning("Chapter file not found: {ChapterPath}. Skipping chapter.", currentChapterPath);
                continue;
            }

            var newChapterPath = Path.Combine(
                managedManga.Library.NotUpscaledLibraryPath,
                PathEscaper.EscapeFileName(newTitle),
                PathEscaper.EscapeFileName(chapter.FileName));

            if (fileSystem.FileExists(newChapterPath))
            {
                logger.LogWarning("Chapter file already exists at target path: {TargetPath}. Cannot rename manga.",
                    newChapterPath);
                canRenameAllChapters = false;
                continue;
            }

            // Check if we can read existing metadata
            ExtractedMetadata? existingMetadata = null;
            try
            {
                existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(currentChapterPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read metadata from {ChapterPath}. Skipping chapter.",
                    currentChapterPath);
                continue;
            }

            string? currentUpscaledPath = null;
            string? newUpscaledPath = null;
            if (chapter.IsUpscaled && managedManga.Library.UpscaledLibraryPath != null)
            {
                currentUpscaledPath = Path.Combine(managedManga.Library.UpscaledLibraryPath, chapter.RelativePath);
                if (fileSystem.FileExists(currentUpscaledPath))
                {
                    newUpscaledPath = Path.Combine(
                        managedManga.Library.UpscaledLibraryPath,
                        PathEscaper.EscapeFileName(newTitle),
                        PathEscaper.EscapeFileName(chapter.FileName));

                    if (fileSystem.FileExists(newUpscaledPath))
                    {
                        logger.LogWarning(
                            "Upscaled chapter file already exists at target path: {TargetPath}. Cannot rename manga.",
                            newUpscaledPath);
                        canRenameAllChapters = false;
                        continue;
                    }
                }
                else
                {
                    currentUpscaledPath = null; // File doesn't exist, skip upscaled operations
                }
            }

            renameOperations.Add(new ChapterRenameOperation
            {
                Chapter = chapter,
                CurrentPath = currentChapterPath,
                NewPath = newChapterPath,
                CurrentUpscaledPath = currentUpscaledPath,
                NewUpscaledPath = newUpscaledPath,
                ExistingMetadata = existingMetadata,
                NewRelativePath = Path.GetRelativePath(manga.Library.NotUpscaledLibraryPath, newChapterPath)
            });
        }

        if (!canRenameAllChapters)
        {
            logger.LogError(
                "Cannot rename manga {MangaId} ({PrimaryTitle}) due to conflicting target files. Aborting rename.",
                manga.Id, manga.PrimaryTitle);
            return RenameResult.Cancelled;
        }

        if (renameOperations.Count == 0)
        {
            logger.LogWarning(
                "No chapters found to rename for manga {MangaId} ({PrimaryTitle}). Proceeding with title change only.",
                manga.Id, manga.PrimaryTitle);
        }

        // Step 2: Perform database operations in a transaction
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Update manga title
            managedManga.ChangePrimaryTitle(newTitle, addOldToAlternative);

            // Update chapter relative paths in the database
            foreach (var operation in renameOperations)
            {
                operation.Chapter.RelativePath = operation.NewRelativePath;
            }

            // Save all database changes
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Database transaction failed during manga rename. Rolling back.");
            return RenameResult.Cancelled;
        }

        // Step 3: If database transaction succeeded, perform file operations
        foreach (var operation in renameOperations)
        {
            try
            {
                // Create target directory if it doesn't exist
                var targetDir = Path.GetDirectoryName(operation.NewPath);
                if (targetDir != null && !Directory.Exists(targetDir))
                {
                    fileSystem.CreateDirectory(targetDir);
                }

                // Update metadata in the source file before moving
                metadataHandling.WriteComicInfo(operation.CurrentPath,
                    operation.ExistingMetadata with { Series = newTitle });

                // Move the main chapter file
                fileSystem.Move(operation.CurrentPath, operation.NewPath);
                _ = chapterChangedNotifier.Notify(operation.Chapter, false);

                // Handle upscaled file if it exists
                if (operation.CurrentUpscaledPath != null && operation.NewUpscaledPath != null)
                {
                    try
                    {
                        var upscaledTargetDir = Path.GetDirectoryName(operation.NewUpscaledPath);
                        if (upscaledTargetDir != null && !Directory.Exists(upscaledTargetDir))
                        {
                            fileSystem.CreateDirectory(upscaledTargetDir);
                        }

                        // Update metadata in upscaled file before moving
                        metadataHandling.WriteComicInfo(operation.CurrentUpscaledPath,
                            metadataHandling.GetSeriesAndTitleFromComicInfo(operation.CurrentUpscaledPath) with
                            {
                                Series = newTitle
                            });

                        // Move the upscaled file
                        fileSystem.Move(operation.CurrentUpscaledPath, operation.NewUpscaledPath);
                        _ = chapterChangedNotifier.Notify(operation.Chapter, true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to move upscaled chapter {FileName} from {SourcePath} to {TargetPath}. Database changes have been committed.",
                            operation.Chapter.FileName, operation.CurrentUpscaledPath, operation.NewUpscaledPath);
                    }
                }

                // Clean up empty source directory
                var sourceDir = Path.GetDirectoryName(operation.CurrentPath);
                if (sourceDir != null)
                {
                    FileSystemHelpers.DeleteIfEmpty(sourceDir, logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to move chapter {FileName} from {SourcePath} to {TargetPath}. Database changes have been committed.",
                    operation.Chapter.FileName, operation.CurrentPath, operation.NewPath);
            }
        }

        // Clean up empty subdirectories in library paths
        _ = Task.Run(() =>
        {
            var pathsToClean = new[] { manga.Library.NotUpscaledLibraryPath, manga.Library.UpscaledLibraryPath }
                .Where(path => path != null)
                .Distinct();

            foreach (var libraryPath in pathsToClean)
            {
                FileSystemHelpers.DeleteEmptySubfolders(libraryPath!, logger);
            }
        });

        return RenameResult.Ok;
    }

    /// <inheritdoc />
    public async Task ChangeChapterTitle(Chapter chapter, string newTitle)
    {
        using var dbContext = dbContextFactory.CreateDbContext();
        
        // Load the chapter with its manga and library
        var managedChapter = await dbContext.Chapters
            .Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(c => c.Id == chapter.Id);
            
        if (managedChapter?.Manga?.Library == null)
        {
            logger.LogWarning("Chapter {ChapterId} not found or missing manga/library data", chapter.Id);
            return;
        }

        try
        {
            metadataHandling.WriteComicInfo(managedChapter.NotUpscaledFullPath,
                metadataHandling.GetSeriesAndTitleFromComicInfo(managedChapter.NotUpscaledFullPath) with
                {
                    ChapterTitle = newTitle
                });

            if (managedChapter.IsUpscaled)
            {
                if (managedChapter.UpscaledFullPath == null)
                {
                    logger.LogWarning("Upscaled chapter file not found: {ChapterPath}", managedChapter.UpscaledFullPath);
                    return;
                }

                metadataHandling.WriteComicInfo(managedChapter.UpscaledFullPath,
                    metadataHandling.GetSeriesAndTitleFromComicInfo(managedChapter.UpscaledFullPath) with
                    {
                        ChapterTitle = newTitle
                    });
            }
        }
        catch (XmlException ex)
        {
            logger.LogWarning(ex, "Error parsing ComicInfo XML for chapter {ChapterId} ({ChapterPath})", managedChapter.Id,
                managedChapter.RelativePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating metadata for chapter {ChapterId} ({ChapterPath})", managedChapter.Id,
                managedChapter.RelativePath);
        }
    }

    private void UpdateChapterTitle(string newTitle, string origChapterPath)
    {
        if (!fileSystem.FileExists(origChapterPath))
        {
            logger.LogWarning("Chapter file not found: {ChapterPath}", origChapterPath);
            return;
        }

        var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(origChapterPath);
        var newMetadata = metadata with { Series = newTitle };
        metadataHandling.WriteComicInfo(origChapterPath, newMetadata);
    }

    private class ChapterRenameOperation
    {
        public required Chapter Chapter { get; set; }
        public required string CurrentPath { get; set; }
        public required string NewPath { get; set; }
        public required string? CurrentUpscaledPath { get; set; }
        public required string? NewUpscaledPath { get; set; }
        public required ExtractedMetadata ExistingMetadata { get; set; }
        public required string NewRelativePath { get; set; }
    }
}