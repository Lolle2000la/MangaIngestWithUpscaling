namespace MangaIngestWithUpscaling.Shared.Constants;

/// <summary>
/// Constants related to image processing and formats
/// </summary>
public static class ImageConstants
{
    /// <summary>
    /// File extensions for supported image formats
    /// </summary>
    public static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif", ".avif"
    };

    /// <summary>
    /// MIME type mappings for supported image formats
    /// </summary>
    public static readonly Dictionary<string, string> MimeTypeMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".webp", "image/webp" },
        { ".bmp", "image/bmp" },
        { ".tiff", "image/tiff" },
        { ".tif", "image/tiff" },
        { ".avif", "image/avif" }
    };

    /// <summary>
    /// Gets the MIME type for a given file extension
    /// </summary>
    /// <param name="extension">File extension (with or without dot)</param>
    /// <returns>MIME type or null if not supported</returns>
    public static string? GetMimeTypeFromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        return MimeTypeMapping.TryGetValue(extension, out var mimeType) ? mimeType : null;
    }

    /// <summary>
    /// Checks if the given file extension is supported
    /// </summary>
    /// <param name="extension">File extension (with or without dot)</param>
    /// <returns>True if the extension is supported</returns>
    public static bool IsSupportedImageExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        return SupportedImageExtensions.Contains(extension);
    }
}