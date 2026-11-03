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

    /// <summary>
    /// Returns the maximum pixel count (width × height) of any single image in the CBZ file.
    /// Returns 0 if the archive contains no supported images or if all dimension reads fail.
    /// </summary>
    /// <param name="cbzPath">Path to the CBZ file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<long> GetMaxPixelCountFromCbzAsync(string cbzPath, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the same per-image preprocessing as <see cref="CreatePreprocessedTempCbzAsync"/>
    /// (max dimension, format conversion, smart downscale) to a single image file. Used by the
    /// page-streaming path so a streamed chapter matches the whole-CBZ path, which preprocesses the
    /// whole archive before upscaling. The preprocessed result replaces the input exactly as it does
    /// there: a format conversion keeps the converted extension (the file is renamed), otherwise the
    /// file is overwritten in place.
    /// </summary>
    /// <param name="imagePath">Path to the image to preprocess.</param>
    /// <param name="options">Preprocessing options to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// The path of the preprocessed image. A format conversion changes the extension, so callers must
    /// use this path rather than <paramref name="imagePath"/> — the original file is deleted when the
    /// extension changes. A page that cannot be decoded is left untouched and its original path is
    /// returned.
    /// </returns>
    Task<string> PreprocessImageInPlaceAsync(
        string imagePath,
        ImagePreprocessingOptions options,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Exercises the native image backend (libvips) end-to-end with a tiny in-memory image, including a
    /// resize and a lossless (.png) and lossy (.jpg) encode/decode round-trip. Throws when the backend
    /// cannot run (missing or mis-versioned native library) or a loader/encoder is missing, so a worker
    /// fails fast at startup instead of silently producing un-preprocessed pages while advertising the
    /// same engine identity as a healthy one.
    /// </summary>
    void VerifyReady();
}
