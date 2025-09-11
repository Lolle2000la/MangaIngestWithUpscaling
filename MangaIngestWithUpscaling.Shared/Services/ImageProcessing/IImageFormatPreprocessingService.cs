namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

/// <summary>
/// Service for preprocessing image formats in CBZ files to ensure all images are of the same type
/// </summary>
public interface IImageFormatPreprocessingService
{
    /// <summary>
    /// Creates a temporary CBZ file where all images are converted to the dominant format
    /// </summary>
    /// <param name="inputCbzPath">Path to the input CBZ file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A disposable wrapper that automatically cleans up the temporary file when disposed</returns>
    Task<TempPreprocessedCbz> CreatePreprocessedTempCbzAsync(string inputCbzPath, CancellationToken cancellationToken);
    
    /// <summary>
    /// Cleans up temporary files created by CreatePreprocessedTempCbzAsync
    /// </summary>
    /// <param name="tempFilePath">Path to the temporary file to delete</param>
    void CleanupTempFile(string tempFilePath);
}