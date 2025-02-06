using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Services.MangaManagement;

[RegisterScoped]
public class MangaMerger(
    ApplicationDbContext dbContext,
    IMetadataHandlingService metadataHandling,
    ILogger<MangaMerger> logger) : IMangaMerger
{
    /// <inheritdoc/>
    public async Task MergeAsync(Manga primary, IEnumerable<Manga> mergedInto, CancellationToken cancellationToken = default)
    {
        if (!dbContext.Entry(primary).Reference(m => m.Library).IsLoaded)
            await dbContext.Entry(primary).Reference(m => m.Library).LoadAsync(cancellationToken);

        foreach (var manga in mergedInto)
        {
            if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
                await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
            if (!dbContext.Entry(manga).Collection(m => m.Chapters).IsLoaded)
                await dbContext.Entry(manga).Collection(m => m.Chapters).LoadAsync(cancellationToken);

            foreach (var chapter in manga.Chapters)
            {
                // change title in metadata to the primary title of the series
                var chapterPath = Path.Combine(manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
                var existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(chapterPath);
                metadataHandling.WriteComicInfo(chapterPath, existingMetadata with { Series = primary.PrimaryTitle });

                // move chapter into the primary mangas library and folder
                var targetPath = Path.Combine(primary.Library.NotUpscaledLibraryPath, primary.PrimaryTitle!, chapter.FileName);
                if (File.Exists(targetPath))
                {
                    logger.LogWarning("Chapter {fileName} already exists in the target path {targetPath}. Skipping.",
                        chapter.FileName, targetPath);
                    continue;
                }
                File.Move(chapterPath, targetPath);
                if (chapter.IsUpscaled && !string.IsNullOrEmpty(manga.Library.UpscaledLibraryPath))
                {
                    var upscaledPath = Path.Combine(manga.Library.UpscaledLibraryPath, chapter.RelativePath);
                    if (File.Exists(upscaledPath))
                    {
                        if (primary.Library.UpscaledLibraryPath != null)
                        {
                            var targetUpscaledPath = Path.Combine(primary.Library.UpscaledLibraryPath, primary.PrimaryTitle!, chapter.FileName);
                            File.Move(upscaledPath, targetUpscaledPath);
                        }
                    }
                }
                chapter.RelativePath = Path.GetRelativePath(primary.Library.NotUpscaledLibraryPath, targetPath);
                primary.Chapters.Add(chapter);
                manga.Chapters.Remove(chapter);
            }

            // This will remove the manga from the library if it has no chapters left
            // If it doesn't then that means that some chapters failed to move and the manga should not be removed
            // This is an case that should be handled by the user with no reasonable way to automatically judge
            // what to do with the manga.
            if (manga.Chapters.Count == 0)
            {
                dbContext.MangaSeries.Remove(manga);

                // remove the folder if it is empty
                var mangaDir = Path.GetDirectoryName(manga.Library.NotUpscaledLibraryPath);
                if (mangaDir != null && Directory.EnumerateFiles(mangaDir).Count() == 0)
                {
                    Directory.Delete(mangaDir);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        _ = Task.Run(() =>
        {
            foreach (var uniqueLibraryPath in mergedInto
                .SelectMany(m => new[] { m.Library.NotUpscaledLibraryPath, m.Library.UpscaledLibraryPath })
                .Where(path => path is not null)
                .Distinct())
            {
                // remove the folder if it is empty
                FileSystemHelpers.DeleteEmpty(uniqueLibraryPath!, logger);
            }
        });
    }
}
