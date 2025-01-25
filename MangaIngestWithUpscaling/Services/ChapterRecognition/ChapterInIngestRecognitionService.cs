using MangaIngestWithUpscaling.Services.MetadataExtraction;
using System.IO.Compression;
using System.Xml.Linq;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition
{
    /// <summary>
    /// Provides services for recognizing chapters in the ingest path.
    /// Not everything in the ingest path is a chapter, so this service
    /// identifies what is and what isn't.
    /// </summary>
    public class ChapterInIngestRecognitionService(
        IMetadataExtractionService metadataExtractionService) : IChapterInIngestRecognitionService
    {
        /// <summary>
        /// Finds all chapters in the ingest path.
        /// </summary>
        /// <param name="ingestPath"></param>
        /// <returns></returns>
        public List<FoundChapter> FindAllChaptersAt(string ingestPath)
        {
            var foundChapters = new List<FoundChapter>();
            // get either all cbz files or all ComicInfo.xml files
            var files = Directory.EnumerateFiles(ingestPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => f.EndsWith(".cbz") || f == "ComicInfo.xml")
                                 .ToList();

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(ingestPath, file);
                var storageType = file.EndsWith(".cbz") ? ChapterStorageType.Cbz : ChapterStorageType.Folder;
                var metadata = metadataExtractionService.GetSeriesAndTitleFromComicInfo(file);

                foundChapters.Add(new FoundChapter(Path.GetFileName(file), relativePath, storageType,
                    metadata));
            }

            return foundChapters;
        }

    }
}
