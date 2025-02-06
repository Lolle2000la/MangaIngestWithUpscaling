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
            var targetPath = Path.Combine(foundIn, chapter.RelativePath);
            var cbzPath = Path.Combine(foundIn, $"{chapter.RelativePath}.cbz");
            var newRelativePath = Path.GetRelativePath(foundIn, cbzPath);
            ZipFile.CreateFromDirectory(targetPath, cbzPath);
            return chapter with
            {
                StorageType = ChapterStorageType.Cbz,
                RelativePath = newRelativePath,
                FileName = Path.GetFileName(cbzPath)
            };
        }

        throw new InvalidOperationException("Chapter is not in a supported format.");
    }
}
