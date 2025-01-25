namespace MangaIngestWithUpscaling.Services.ChapterRecognition
{
    public interface IChapterInIngestRecognitionService
    {
        List<FoundChapter> FindAllChaptersAt(string ingestPath);
    }
}
