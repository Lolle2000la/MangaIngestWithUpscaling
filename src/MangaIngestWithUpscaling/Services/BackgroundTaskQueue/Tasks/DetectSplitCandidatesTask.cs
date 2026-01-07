using System.IO.Compression;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class DetectSplitCandidatesTask : BaseTask
{
    public int ChapterId { get; set; }
    public int DetectorVersion { get; set; }
    public string FriendlyEntryName { get; set; } = string.Empty;

    public override int RetryFor { get; set; } = 1;

    public DetectSplitCandidatesTask() { }

    public DetectSplitCandidatesTask(int chapterId, int detectorVersion)
    {
        ChapterId = chapterId;
        DetectorVersion = detectorVersion;
    }

    public DetectSplitCandidatesTask(Chapter chapter, int detectorVersion)
    {
        ChapterId = chapter.Id;
        DetectorVersion = detectorVersion;
        FriendlyEntryName =
            $"Detecting splits for {chapter.FileName} of {chapter.Manga?.PrimaryTitle ?? "Unknown"}";
    }

    public override async Task ProcessAsync(
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var splitDetectionService = services.GetRequiredService<ISplitDetectionService>();
        var splitProcessingService = services.GetRequiredService<ISplitProcessingService>();
        var logger = services.GetRequiredService<ILogger<DetectSplitCandidatesTask>>();
        var localizer = services.GetRequiredService<IStringLocalizer<DetectSplitCandidatesTask>>();

        var chapter = await dbContext
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(c => c.Id == ChapterId, cancellationToken);

        if (chapter == null)
        {
            throw new InvalidOperationException(localizer["Error_ChapterNotFound", ChapterId]);
        }

        var libraryPath = chapter.Manga.Library.NotUpscaledLibraryPath;
        var chapterPath = Path.Combine(libraryPath, chapter.RelativePath);

        // Create a temp directory for extraction
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "mangaingest_split_detection",
            Guid.NewGuid().ToString()
        );
        Directory.CreateDirectory(tempDir);
        bool isTempDir = true;

        try
        {
            if (File.Exists(chapterPath))
            {
                // It's a file (CBZ/ZIP), extract it
                logger.LogInformation(
                    "Extracting chapter {ChapterPath} to {TempDir}",
                    chapterPath,
                    tempDir
                );
                using var archive = ZipFile.OpenRead(chapterPath);
                foreach (var entry in archive.Entries)
                {
                    if (IsImage(entry.Name))
                    {
                        var destPath = Path.Combine(tempDir, entry.Name);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (destDir != null)
                            Directory.CreateDirectory(destDir);
                        entry.ExtractToFile(destPath);
                    }
                }
            }
            else if (Directory.Exists(chapterPath))
            {
                // It's a directory, use it directly
                // But wait, if we use it directly, we might modify it?
                // Detection is read-only.
                // So we can use it directly.
                if (Directory.GetFiles(tempDir).Length == 0) // If temp dir is empty (we didn't extract anything)
                {
                    Directory.Delete(tempDir); // Delete the unused temp dir
                    tempDir = chapterPath;
                    isTempDir = false;
                }
            }
            else
            {
                throw new FileNotFoundException(
                    localizer["Error_ChapterFileOrFolderNotFound", chapterPath]
                );
            }

            var progressReporter = new Progress<UpscaleProgress>(p =>
            {
                if (p.Total.HasValue)
                {
                    this.Progress.Total = p.Total.Value;
                }
                if (p.Current.HasValue)
                {
                    this.Progress.Current = p.Current.Value;
                }
            });

            var results = await splitDetectionService.DetectSplitsAsync(
                tempDir,
                progressReporter,
                cancellationToken
            );

            await splitProcessingService.ProcessDetectionResultsAsync(
                ChapterId,
                results,
                DetectorVersion,
                cancellationToken
            );
        }
        finally
        {
            if (isTempDir && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete temp directory {TempDir}", tempDir);
                }
            }
        }
    }

    private bool IsImage(string filename)
    {
        var ext = Path.GetExtension(filename);
        return ImageConstants.SupportedImageExtensions.Contains(ext);
    }
}
