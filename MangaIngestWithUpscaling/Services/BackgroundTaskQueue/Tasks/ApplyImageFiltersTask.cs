using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Background task to retroactively apply image filters to existing chapters in a library.
/// </summary>
public class ApplyImageFiltersTask : BaseTask
{
    public int LibraryId { get; set; }

    /// <summary>
    /// If true, only process chapters that haven't been processed by image filters yet.
    /// If false, process all chapters regardless.
    /// </summary>
    public bool OnlyUnprocessed { get; set; } = true;

    public override string TaskFriendlyName => $"Apply Image Filters to Library (ID: {LibraryId})";

    public override async Task ProcessAsync(
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var imageFilterService = scope.ServiceProvider.GetRequiredService<IImageFilterService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplyImageFiltersTask>>();

        // Load the library and its filtered images
        var library = await dbContext
            .Libraries.Include(l => l.FilteredImages)
            .FirstOrDefaultAsync(l => l.Id == LibraryId, cancellationToken);

        if (library == null)
        {
            logger.LogWarning("Library with ID {LibraryId} not found", LibraryId);
            return;
        }

        if (!library.FilteredImages.Any())
        {
            logger.LogInformation(
                "No filtered images configured for library {LibraryName}",
                library.Name
            );
            return;
        }

        logger.LogInformation(
            "Starting image filter application for library {LibraryName} with {FilterCount} filters",
            library.Name,
            library.FilteredImages.Count
        );

        // Get all chapters in the library
        var chaptersQuery = dbContext
            .Chapters.Include(c => c.Manga)
            .Where(c => c.Manga.LibraryId == LibraryId);

        var totalChapters = await chaptersQuery.CountAsync(cancellationToken);
        logger.LogInformation("Found {TotalChapters} chapters to process", totalChapters);

        // Initialize progress reporting (units: chapters)
        Progress.ProgressUnit = "chapters";
        Progress.Total = totalChapters;
        Progress.Current = 0;
        Progress.StatusMessage =
            totalChapters == 0
                ? "No chapters to process"
                : $"Processing 0/{totalChapters} chapters";

        int processedChapters = 0;
        int filteredImages = 0;

        await foreach (
            var chapter in chaptersQuery.AsAsyncEnumerable().WithCancellation(cancellationToken)
        )
        {
            try
            {
                // Build paths for original and upscaled chapters
                var originalPath = chapter.NotUpscaledFullPath;
                string? upscaledPath = null;

                if (chapter.IsUpscaled && !string.IsNullOrEmpty(library.UpscaledLibraryPath))
                {
                    upscaledPath = chapter.UpscaledFullPath;
                    if (!File.Exists(upscaledPath))
                    {
                        upscaledPath = null; // Don't pass invalid path
                    }
                }

                // Apply filters to both original and upscaled using the new optimized method
                if (File.Exists(originalPath))
                {
                    var result = await imageFilterService.ApplyFiltersToChapterAsync(
                        originalPath,
                        upscaledPath,
                        library.FilteredImages,
                        cancellationToken
                    );
                    filteredImages += result.FilteredCount;

                    if (result.FilteredCount > 0)
                    {
                        var message =
                            upscaledPath != null
                                ? $"Filtered {result.FilteredCount} images from chapter {chapter.FileName} (both original and upscaled)"
                                : $"Filtered {result.FilteredCount} images from original chapter {chapter.FileName}";
                        logger.LogInformation(message);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Original chapter file not found: {OriginalPath}",
                        originalPath
                    );
                }

                processedChapters++;

                // Update progress after each chapter
                Progress.Current = processedChapters;
                Progress.StatusMessage = $"Processed: {chapter.FileName}";

                if (processedChapters % 10 == 0)
                {
                    logger.LogInformation(
                        "Progress: {ProcessedChapters}/{TotalChapters} chapters processed, {FilteredImages} images filtered",
                        processedChapters,
                        totalChapters,
                        filteredImages
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing chapter {ChapterFile}", chapter.FileName);
            }
        }

        // Update occurrence counts for filtered images
        foreach (var filteredImage in library.FilteredImages.Where(f => f.OccurrenceCount > 0))
        {
            filteredImage.LastMatchedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Completed image filter application for library {LibraryName}. "
                + "Processed {ProcessedChapters} chapters, filtered {FilteredImages} images total",
            library.Name,
            processedChapters,
            filteredImages
        );

        // Final progress update
        Progress.Current = processedChapters;
        Progress.StatusMessage =
            $"Completed: {processedChapters}/{totalChapters} chapters | Filtered images total: {filteredImages}";
    }
}
