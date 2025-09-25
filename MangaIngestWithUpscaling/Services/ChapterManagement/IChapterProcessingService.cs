using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

/// <summary>
/// Interface for shared chapter processing operations used by both IngestProcessor and LibraryIntegrityChecker.
/// </summary>
public interface IChapterProcessingService
{
    /// <summary>
    /// Detects if a chapter file is upscaled based on upscaler.json presence and path patterns.
    /// </summary>
    /// <param name="filePath">Full path to the chapter file</param>
    /// <param name="relativePath">Relative path of the chapter file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple indicating if the file is upscaled and the upscaler profile DTO if found</returns>
    Task<(bool IsUpscaled, UpscalerProfileJsonDto? UpscalerProfile)> DetectUpscaledFileAsync(
        string filePath, string relativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an existing upscaler profile or creates a new one based on the DTO.
    /// </summary>
    /// <param name="dto">The upscaler profile DTO</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The upscaler profile, or null if creation failed</returns>
    Task<UpscalerProfile?> FindOrCreateUpscalerProfileAsync(UpscalerProfileJsonDto dto, CancellationToken cancellationToken);

    /// <summary>
    /// Finds or creates a manga series entity.
    /// </summary>
    /// <param name="library">The library</param>
    /// <param name="seriesTitle">The series title</param>
    /// <param name="originalSeriesTitle">The original series title (for alternative titles)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The manga series entity</returns>
    Task<Manga> GetOrCreateMangaSeriesAsync(Library library, string seriesTitle, string? originalSeriesTitle, CancellationToken cancellationToken);

    /// <summary>
    /// Moves an upscaled file to the correct upscaled library location.
    /// </summary>
    /// <param name="sourcePath">Source file path</param>
    /// <param name="library">The library</param>
    /// <param name="targetRelativePath">Target relative path in upscaled library</param>
    /// <param name="cancellationToken">Cancellation token</param>
    void MoveUpscaledFileToLibrary(string sourcePath, Library library, string targetRelativePath, CancellationToken cancellationToken);
}