using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.FileSystem;

namespace MangaIngestWithUpscaling.Services.MangaManagement;

[RegisterScoped]
public class MangaLibraryMover(
    ILogger<MangaLibraryMover> logger,
    ApplicationDbContext dbContext,
    ITaskQueue taskQueue) : IMangaLibraryMover
{
    public async Task MoveMangaAsync(Manga manga, Library targetLibrary, CancellationToken cancellationToken = default)
    {
        // A manga library change is very close to a rename in how it is handled.
        // But it is a bit simpler because we don't need to change any metadata.

        if (manga.LibraryId == targetLibrary.Id)
        {
            // The manga is already in the target library. Nothing to do.
            return;
        }

        // first, let's load the chapters and the library if they are not already loaded.
        if (!dbContext.Entry(manga).Collection(m => m.Chapters).IsLoaded)
        {
            await dbContext.Entry(manga).Collection(m => m.Chapters).LoadAsync(cancellationToken);
        }
        if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
        }

        // We should keep the old library for later use.
        var oldLibrary = manga.Library;
        // Now we can change the library references on the manga entity.
        manga.Library = targetLibrary;
        manga.LibraryId = targetLibrary.Id;
        
        dbContext.Update(manga);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Let's get all the target paths we need and ensure they exist.
        // This is where we will move the chapters to.
        var targetNotUpscaledLibraryPath = Path.Combine(targetLibrary.NotUpscaledLibraryPath, manga.PrimaryTitle);
        if (!Directory.Exists(targetNotUpscaledLibraryPath))
        {
            Directory.CreateDirectory(targetNotUpscaledLibraryPath);
        }
        var targetUpscaledLibraryPath = targetLibrary.UpscaledLibraryPath == null 
            ? null : Path.Combine(targetLibrary.UpscaledLibraryPath, manga.PrimaryTitle);
        if (!Directory.Exists(targetUpscaledLibraryPath))
        {
            Directory.CreateDirectory(targetUpscaledLibraryPath);
        }

        // Now we can move the chapters that are not upscaled.
        foreach (var chapter in manga.Chapters)
        {
            // The simple part is moving the not-upscaled mangas.
            // for that we should first create the target directory if it doesn't exist.
            var sourcePath = Path.Combine(oldLibrary.NotUpscaledLibraryPath, chapter.RelativePath);
            var targetPath = Path.Combine(targetNotUpscaledLibraryPath, chapter.FileName);
            try
            {
                File.Move(sourcePath, targetPath);
            }
            catch (Exception ex)
            {
                // We should log the error and continue with the next chapter.
                logger.LogError(ex, "Failed to move chapter {fileName} from {sourcePath} to {targetPath}.",
                    chapter.FileName, sourcePath, targetPath);
                continue;
            }

            // We should update the chapter's relative path to reflect the new location.
            chapter.RelativePath = Path.GetRelativePath(targetLibrary.NotUpscaledLibraryPath, targetPath);
            // we can also check if we can delete the old directory if empty.
            FileSystemHelpers.DeleteIfEmpty(Path.GetDirectoryName(sourcePath)!, logger);


            // Now we should move the upscaled chapters. Here we have this quite annoying problem that we can't just move the files.
            // They may still be upscaling or have other tasks pending. So what we will do is to enqueue a task to "rename" the chapters.
            // "Renaming" here means to change the metadata to reflect the new title and move the file to the new location, though there is
            // no actual changing of the title so it is more like a move, but allows us to reuse the existing task.

            if (!chapter.IsUpscaled) continue; // phew, we don't have to do anything for this chapter.

            if (oldLibrary.UpscaledLibraryPath == null)
            {
                logger.LogWarning("Upscaled library path not set for library {LibraryId} even though chapter {ChapterId} is supposed to be upscaled within it.", oldLibrary.Id, chapter.Id);
                continue;
            }

            var sourceUpscaledPath = Path.Combine(oldLibrary.UpscaledLibraryPath, chapter.RelativePath); // this is again only a calculated property.

            // Let's make sure we have all the paths we need. If not, we should log a warning and continue with the next chapter.
            if (sourceUpscaledPath == null)
            {
                logger.LogWarning("Upscaled path for chapter {chapterId} is null. Skipping.", chapter.Id);
                continue;
            }

            if (targetUpscaledLibraryPath == null)
            {
                logger.LogWarning("Target library {libraryId} does not have an upscaled library path. Skipping.", targetLibrary.Id);
                continue;
            }

            if (!File.Exists(sourceUpscaledPath))
            {
                logger.LogWarning("Upscaled chapter file not found: {ChapterPath}", sourceUpscaledPath);
                continue;
            }

            // Now we can enqueue the task to rename the chapter.
            await taskQueue.EnqueueAsync(
                new RenameUpscaledChaptersSeriesTask(chapter.Id, sourceUpscaledPath, manga.PrimaryTitle));
        }

        // Now we can save the changes to the database.
        await dbContext.SaveChangesAsync(cancellationToken);
        // And we are done. The chapters are moved and the database is updated.
    }
}
