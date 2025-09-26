using System.Text.RegularExpressions;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

/// <summary>
/// Shared service for common chapter processing operations used by both IngestProcessor and LibraryIntegrityChecker.
/// </summary>
[RegisterScoped]
public partial class ChapterProcessingService(
    ApplicationDbContext dbContext,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    IFileSystem fileSystem,
    ILogger<ChapterProcessingService> logger
) : IChapterProcessingService
{
    /// <summary>
    /// Detects if a chapter file is upscaled based on upscaler.json presence and path patterns.
    /// </summary>
    /// <param name="filePath">Full path to the chapter file</param>
    /// <param name="relativePath">Relative path of the chapter file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple indicating if the file is upscaled and the upscaler profile DTO if found</returns>
    public async Task<(
        bool IsUpscaled,
        UpscalerProfileJsonDto? UpscalerProfile
    )> DetectUpscaledFileAsync(
        string filePath,
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        // First check for upscaler.json - this overrides path-based detection
        var upscalerProfileDto = await upscalerJsonHandlingService.ReadUpscalerJsonAsync(
            filePath,
            cancellationToken
        );
        if (upscalerProfileDto != null)
        {
            return (true, upscalerProfileDto);
        }

        // Fall back to path-based detection
        bool isUpscaledByPath = IsUpscaledChapterRegex().IsMatch(relativePath);
        return (isUpscaledByPath, null);
    }

    /// <summary>
    /// Finds an existing upscaler profile or creates a new one based on the DTO.
    /// </summary>
    /// <param name="dto">The upscaler profile DTO</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The upscaler profile, or null if creation failed</returns>
    public async Task<UpscalerProfile?> FindOrCreateUpscalerProfileAsync(
        UpscalerProfileJsonDto dto,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Try to find an existing profile with matching characteristics
            var existingProfile = await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
                p =>
                    p.Name == dto.Name
                    && p.UpscalerMethod == dto.UpscalerMethod
                    && (int)p.ScalingFactor == dto.ScalingFactor,
                cancellationToken
            );

            if (existingProfile != null)
            {
                return existingProfile;
            }

            // For now, we'll just log that we found the profile but not create it automatically
            // as this might require more validation and user approval
            logger.LogWarning(
                "Upscaler profile '{ProfileName}' not found in database. Manual creation may be required.",
                dto.Name
            );

            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error finding or creating upscaler profile for {ProfileName}",
                dto.Name
            );
            return null;
        }
    }

    /// <summary>
    /// Finds or creates a manga series entity.
    /// </summary>
    /// <param name="library">The library</param>
    /// <param name="seriesTitle">The series title</param>
    /// <param name="originalSeriesTitle">The original series title (for alternative titles)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The manga series entity</returns>
    public async Task<Manga> GetOrCreateMangaSeriesAsync(
        Library library,
        string seriesTitle,
        string? originalSeriesTitle,
        CancellationToken cancellationToken
    )
    {
        // Try to find existing series (also considering alternate names)
        var seriesEntity = await dbContext
            .MangaSeries.Include(s => s.OtherTitles)
            .Include(s => s.Chapters)
            .Include(s => s.UpscalerProfilePreference)
            .FirstOrDefaultAsync(
                s =>
                    s.LibraryId == library.Id
                    && (
                        s.PrimaryTitle == seriesTitle
                        || s.OtherTitles.Any(an => an.Title == seriesTitle)
                    ),
                cancellationToken
            );

        if (seriesEntity == null)
        {
            seriesEntity = new Manga
            {
                PrimaryTitle = seriesTitle,
                OtherTitles = new List<MangaAlternativeTitle>(),
                Library = library,
                LibraryId = library.Id,
                Chapters = new List<Chapter>(),
            };

            // Add original as alternative title if different
            if (
                !string.IsNullOrEmpty(originalSeriesTitle)
                && !string.Equals(originalSeriesTitle, seriesTitle, StringComparison.Ordinal)
            )
            {
                seriesEntity.OtherTitles.Add(
                    new MangaAlternativeTitle { Manga = seriesEntity, Title = originalSeriesTitle }
                );
            }

            dbContext.MangaSeries.Add(seriesEntity);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Created new manga series '{SeriesTitle}' in library '{LibraryName}'",
                seriesTitle,
                library.Name
            );
        }

        // Ensure collections are initialized
        seriesEntity.Chapters ??= new List<Chapter>();
        seriesEntity.OtherTitles ??= new List<MangaAlternativeTitle>();

        return seriesEntity;
    }

    /// <summary>
    /// Moves an upscaled file to the correct upscaled library location.
    /// </summary>
    /// <param name="sourcePath">Source file path</param>
    /// <param name="library">The library</param>
    /// <param name="targetRelativePath">Target relative path in upscaled library</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void MoveUpscaledFileToLibrary(
        string sourcePath,
        Library library,
        string targetRelativePath,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(library.UpscaledLibraryPath))
        {
            throw new InvalidOperationException("Library has no upscaled path configured");
        }

        try
        {
            string targetPath = Path.Combine(library.UpscaledLibraryPath, targetRelativePath);
            string targetDirectory = Path.GetDirectoryName(targetPath)!;

            // Create target directory if it doesn't exist
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // Move the file
            fileSystem.Move(sourcePath, targetPath);

            logger.LogInformation(
                "Moved upscaled file from '{SourcePath}' to '{TargetPath}' in library '{LibraryName}'",
                sourcePath,
                targetPath,
                library.Name
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to move upscaled file from '{SourcePath}' to upscaled library path in library '{LibraryName}'",
                sourcePath,
                library.Name
            );
            throw; // Re-throw to indicate the operation failed
        }
    }

    /// <summary>
    /// Gets the canonical path by removing _upscaled folder components.
    /// </summary>
    /// <param name="relativePath">The relative path to canonicalize</param>
    /// <returns>The canonical path</returns>
    public static string GetCanonicalPath(string relativePath)
    {
        return IsUpscaledChapterRegex().Replace(relativePath, "");
    }

    /// <summary>
    /// Regular expression to detect upscaled chapters (matches _upscaled folder in path).
    /// </summary>
    [GeneratedRegex(@"(?:^|[\\/])_upscaled(?=[\\/])")]
    private static partial Regex IsUpscaledChapterRegex();
}
