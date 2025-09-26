using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Shared.Services.CbzConversion;

public interface ICbzConverter
{
    FoundChapter ConvertToCbz(FoundChapter chapter, string foundIn);
}
