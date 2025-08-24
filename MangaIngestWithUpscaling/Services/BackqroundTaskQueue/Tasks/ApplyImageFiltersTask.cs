using System.Text.Json.Serialization;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

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

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var imageFilterService = scope.ServiceProvider.GetRequiredService<IImageFilterService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplyImageFiltersTask>>();

        // Load the library and its filtered images
        var library = await dbContext.Libraries
            .Include(l => l.FilteredImages)
            .FirstOrDefaultAsync(l => l.Id == LibraryId, cancellationToken);

        if (library == null)
        {
            logger.LogWarning("Library with ID {LibraryId} not found", LibraryId);
            return;
        }

        if (!library.FilteredImages.Any())
        {
            logger.LogInformation("No filtered images configured for library {LibraryName}", library.Name);
            return;
        }

        logger.LogInformation("Starting image filter application for library {LibraryName} with {FilterCount} filters", 
            library.Name, library.FilteredImages.Count);

        // Get all chapters in the library
        var chaptersQuery = dbContext.Chapters
            .Include(c => c.Manga)
            .Where(c => c.Manga.LibraryId == LibraryId);

        var totalChapters = await chaptersQuery.CountAsync(cancellationToken);
        logger.LogInformation("Found {TotalChapters} chapters to process", totalChapters);

        int processedChapters = 0;
        int filteredImages = 0;

        await foreach (var chapter in chaptersQuery.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            try
            {
                // Process original chapter
                var originalPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.Manga.PrimaryTitle!, chapter.FileName);
                if (File.Exists(originalPath))
                {
                    var originalResult = await imageFilterService.ApplyFiltersToChapterAsync(originalPath, library.FilteredImages, cancellationToken);
                    filteredImages += originalResult.FilteredCount;
                    
                    if (originalResult.FilteredCount > 0)
                    {
                        logger.LogInformation("Filtered {Count} images from original chapter {ChapterFile}", 
                            originalResult.FilteredCount, chapter.FileName);
                    }
                }

                // Process upscaled chapter if it exists
                if (chapter.IsUpscaled && !string.IsNullOrEmpty(library.UpscaledLibraryPath))
                {
                    var upscaledPath = Path.Combine(library.UpscaledLibraryPath, chapter.Manga.PrimaryTitle!, chapter.FileName);
                    if (File.Exists(upscaledPath))
                    {
                        var upscaledResult = await imageFilterService.ApplyFiltersToChapterAsync(upscaledPath, library.FilteredImages, cancellationToken);
                        filteredImages += upscaledResult.FilteredCount;
                        
                        if (upscaledResult.FilteredCount > 0)
                        {
                            logger.LogInformation("Filtered {Count} images from upscaled chapter {ChapterFile}", 
                                upscaledResult.FilteredCount, chapter.FileName);
                        }
                    }
                }

                processedChapters++;
                
                if (processedChapters % 10 == 0)
                {
                    logger.LogInformation("Progress: {ProcessedChapters}/{TotalChapters} chapters processed, {FilteredImages} images filtered", 
                        processedChapters, totalChapters, filteredImages);
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

        logger.LogInformation("Completed image filter application for library {LibraryName}. " +
                              "Processed {ProcessedChapters} chapters, filtered {FilteredImages} images total", 
            library.Name, processedChapters, filteredImages);
    }
}
