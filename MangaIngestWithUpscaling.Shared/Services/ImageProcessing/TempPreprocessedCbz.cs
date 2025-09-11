namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

/// <summary>
/// Disposable wrapper for a temporary preprocessed CBZ file that automatically cleans up when disposed
/// </summary>
public sealed class TempPreprocessedCbz : IDisposable
{
    private readonly IImageFormatPreprocessingService _preprocessingService;
    private bool _disposed;

    public string FilePath { get; }

    internal TempPreprocessedCbz(string filePath, IImageFormatPreprocessingService preprocessingService)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _preprocessingService = preprocessingService ?? throw new ArgumentNullException(nameof(preprocessingService));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _preprocessingService.CleanupTempFile(FilePath);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}