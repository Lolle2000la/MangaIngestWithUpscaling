using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public interface IMetadataHandlingService
{
    ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file);
    void WriteComicInfo(string file, ExtractedMetadata metadata);
}
