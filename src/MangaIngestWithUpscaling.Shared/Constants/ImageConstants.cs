namespace MangaIngestWithUpscaling.Shared.Constants;

/// <summary>
/// Constants related to image processing and formats
/// </summary>
public static class ImageConstants
{
    /// <summary>
    /// File extensions for supported image formats
    /// </summary>
    public static readonly HashSet<string> SupportedImageExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".tiff",
        ".tif",
        ".avif",
    };

    /// <summary>
    /// MIME type mappings for supported image formats
    /// </summary>
    public static readonly Dictionary<string, string> MimeTypeMapping = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".webp", "image/webp" },
        { ".bmp", "image/bmp" },
        { ".tiff", "image/tiff" },
        { ".tif", "image/tiff" },
        { ".avif", "image/avif" },
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

        extension = extension.ToLowerInvariant();

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        return SupportedImageExtensions.Contains(extension);
    }

    /// <summary>
    /// Detects the image format from the file header (magic bytes)
    /// </summary>
    /// <param name="fileBytes">The first few bytes of the file (at least 12 bytes recommended)</param>
    /// <returns>The detected file extension (with dot) or null if format is not recognized</returns>
    public static string? DetectImageFormatFromHeader(ReadOnlySpan<byte> fileBytes)
    {
        if (fileBytes.Length < 2)
            return null;

        // JPEG: FF D8 FF
        if (
            fileBytes[0] == 0xFF
            && fileBytes[1] == 0xD8
            && fileBytes.Length >= 3
            && fileBytes[2] == 0xFF
        )
            return ".jpg";

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (
            fileBytes.Length >= 8
            && fileBytes[0] == 0x89
            && fileBytes[1] == 0x50
            && fileBytes[2] == 0x4E
            && fileBytes[3] == 0x47
            && fileBytes[4] == 0x0D
            && fileBytes[5] == 0x0A
            && fileBytes[6] == 0x1A
            && fileBytes[7] == 0x0A
        )
            return ".png";

        // WebP: "RIFF" followed by file size, then "WEBP"
        if (
            fileBytes.Length >= 12
            && fileBytes[0] == 0x52
            && // R
            fileBytes[1] == 0x49
            && // I
            fileBytes[2] == 0x46
            && // F
            fileBytes[3] == 0x46
            && // F
            fileBytes[8] == 0x57
            && // W
            fileBytes[9] == 0x45
            && // E
            fileBytes[10] == 0x42
            && // B
            fileBytes[11] == 0x50
        ) // P
            return ".webp";

        // BMP: "BM"
        if (fileBytes[0] == 0x42 && fileBytes[1] == 0x4D)
            return ".bmp";

        // TIFF: "II" (little-endian) or "MM" (big-endian)
        if (fileBytes.Length >= 4)
        {
            if (
                (
                    fileBytes[0] == 0x49
                    && fileBytes[1] == 0x49
                    && fileBytes[2] == 0x2A
                    && fileBytes[3] == 0x00
                )
                || (
                    fileBytes[0] == 0x4D
                    && fileBytes[1] == 0x4D
                    && fileBytes[2] == 0x00
                    && fileBytes[3] == 0x2A
                )
            )
                return ".tiff";
        }

        // AVIF: starts with ftyp box
        if (fileBytes.Length >= 12)
        {
            // Check for ftyp box (bytes 4-7 should be "ftyp")
            if (
                fileBytes[4] == 0x66
                && // f
                fileBytes[5] == 0x74
                && // t
                fileBytes[6] == 0x79
                && // y
                fileBytes[7] == 0x70
            ) // p
            {
                // Check for avif brand (bytes 8-11)
                if (
                    (
                        fileBytes[8] == 0x61
                        && fileBytes[9] == 0x76
                        && fileBytes[10] == 0x69
                        && fileBytes[11] == 0x66
                    )
                    || // avif
                    (
                        fileBytes.Length >= 16
                        && fileBytes[12] == 0x61
                        && fileBytes[13] == 0x76
                        && fileBytes[14] == 0x69
                        && fileBytes[15] == 0x66
                    )
                ) // avif in compatible brands
                    return ".avif";
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the image format from a file and returns the correct extension
    /// </summary>
    /// <param name="filePath">Path to the image file</param>
    /// <returns>The detected file extension (with dot) or null if format is not recognized</returns>
    public static string? DetectImageFormatFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using var fileStream = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[16];
            int bytesRead = fileStream.Read(header);
            return DetectImageFormatFromHeader(header[..bytesRead]);
        }
        catch
        {
            return null;
        }
    }
}
