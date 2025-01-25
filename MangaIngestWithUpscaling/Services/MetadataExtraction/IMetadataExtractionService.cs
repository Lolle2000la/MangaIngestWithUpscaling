namespace MangaIngestWithUpscaling.Services.MetadataExtraction
{
    public interface IMetadataExtractionService
    {
        ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file);
    }
}
