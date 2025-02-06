using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Services.CbzConversion;

[RegisterScoped]
public class CbzConverter : ICbzConverter
{
    public FoundChapter ConvertToCbz(FoundChapter chapter, string foundIn)
    {
        if (chapter.StorageType == ChapterStorageType.Cbz) return chapter;

        if (chapter.StorageType == ChapterStorageType.Folder)
        {
            var cbzPath = Path.Combine(foundIn, $"{chapter.RelativePath}.cbz");
            var targetPath = Path.Combine(foundIn, chapter.RelativePath);
            ZipFile.CreateFromDirectory(targetPath, cbzPath);
            return chapter with
            {
                StorageType = ChapterStorageType.Cbz,
                RelativePath = targetPath,
                FileName = Path.GetFileName(targetPath)
            };
        }

        throw new InvalidOperationException("Chapter is not in a supported format.");
    }
}
