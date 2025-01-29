using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.MetadataExtraction;

public interface IMetadataHandlingService
{
    ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file);
    void WriteComicInfo(string file, ExtractedMetadata metadata);
}
