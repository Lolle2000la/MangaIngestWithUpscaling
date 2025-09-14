using NetVips;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

/// <summary>
/// Helper class to convert between NetVips and ImageSharp formats for compatibility with CoenM.ImageHash
/// </summary>
public static class ImageHashAdapter
{
    /// <summary>
    /// Converts NetVips image data to ImageSharp Image&lt;Rgba32&gt; for perceptual hash calculation
    /// This is only used for hash calculation to maintain compatibility with CoenM.ImageHash
    /// </summary>
    /// <param name="imageBytes">Raw image bytes</param>
    /// <returns>ImageSharp Image for hash calculation</returns>
    public static SixLabors.ImageSharp.Image<Rgba32> ConvertToImageSharpForHash(byte[] imageBytes)
    {
        // Use ImageSharp only for the hash calculation to maintain compatibility
        // with existing perceptual hashes in the database
        return SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
    }
}