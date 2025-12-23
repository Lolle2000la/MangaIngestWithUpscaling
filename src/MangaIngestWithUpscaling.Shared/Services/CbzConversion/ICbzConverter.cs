using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Shared.Services.CbzConversion;

public interface ICbzConverter
{
    FoundChapter ConvertToCbz(FoundChapter chapter, string foundIn);

    /// <summary>
    /// Fixes image file extensions in an existing CBZ file to match actual file formats.
    /// </summary>
    /// <param name="cbzPath">Path to the CBZ file to fix</param>
    /// <returns>True if any extensions were corrected, false otherwise</returns>
    bool FixImageExtensionsInCbz(string cbzPath);
}
