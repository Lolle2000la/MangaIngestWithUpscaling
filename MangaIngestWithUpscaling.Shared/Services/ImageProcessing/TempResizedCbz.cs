namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

/// <summary>
/// Disposable wrapper for a temporary resized CBZ file that automatically cleans up when disposed
/// </summary>
public sealed class TempResizedCbz : IDisposable
{
    private readonly IImageResizeService _imageResizeService;
    private bool _disposed;

    public string FilePath { get; }

    internal TempResizedCbz(string filePath, IImageResizeService imageResizeService)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _imageResizeService = imageResizeService ?? throw new ArgumentNullException(nameof(imageResizeService));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _imageResizeService.CleanupTempFile(FilePath);
            _disposed = true;
        }
    }
}
