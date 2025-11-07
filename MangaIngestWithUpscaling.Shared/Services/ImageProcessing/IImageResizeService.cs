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
    /// <returns>A disposable wrapper that automatically cleans up the temporary file when disposed</returns>
    Task<TempResizedCbz> CreateResizedTempCbzAsync(
        string inputCbzPath,
        int maxDimension,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a temporary CBZ file with images preprocessed according to the provided options
    /// (resizing and/or format conversion)
    /// </summary>
    /// <param name="inputCbzPath">Path to the input CBZ file</param>
    /// <param name="options">Preprocessing options to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A disposable wrapper that automatically cleans up the temporary file when disposed</returns>
    Task<TempResizedCbz> CreatePreprocessedTempCbzAsync(
        string inputCbzPath,
        ImagePreprocessingOptions options,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Cleans up temporary files created by CreateResizedTempCbzAsync
    /// </summary>
    /// <param name="tempFilePath">Path to the temporary file to delete</param>
    void CleanupTempFile(string tempFilePath);
}
