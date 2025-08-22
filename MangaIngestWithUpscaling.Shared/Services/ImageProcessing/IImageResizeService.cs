namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

/// <summary>
/// Service for resizing images while maintaining aspect ratio
/// </summary>
public interface IImageResizeService
{
    /// <summary>
    /// Creates a temporary resized CBZ file where all images are resized to fit within the specified maximum dimension
    /// </summary>
    /// <param name="inputCbzPath">Path to the input CBZ file</param>
    /// <param name="maxDimension">Maximum width or height dimension</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the temporary resized CBZ file</returns>
    Task<string> CreateResizedTempCbzAsync(string inputCbzPath, int maxDimension, CancellationToken cancellationToken);
    
    /// <summary>
    /// Cleans up temporary files created by CreateResizedTempCbzAsync
    /// </summary>
    /// <param name="tempFilePath">Path to the temporary file to delete</param>
    void CleanupTempFile(string tempFilePath);
}
