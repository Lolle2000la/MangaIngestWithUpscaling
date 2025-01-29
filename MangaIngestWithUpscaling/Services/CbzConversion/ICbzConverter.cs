using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.CbzConversion;

public interface ICbzConverter
{
    FoundChapter ConvertToCbz(FoundChapter chapter, string foundIn);
}
