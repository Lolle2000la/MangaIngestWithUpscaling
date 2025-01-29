using System.IO.Compression;
using System.Xml.Linq;

namespace MangaIngestWithUpscaling.Services.MetadataExtraction;

public class MetadataExtractionService : IMetadataExtractionService
{
    public ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file)
    {
        var title = string.Empty;
        var series = string.Empty;
        if (file.EndsWith(".cbz"))
        {
            using var archive = ZipFile.OpenRead(file);
            var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
            if (comicInfoEntry != null)
            {
                using var stream = comicInfoEntry.Open();
                var document = XDocument.Load(stream);
                var titleElement = document.Root.Element("Title");
                if (titleElement != null)
                {
                    title = titleElement.Value;
                }
                var seriesElement = document.Root.Element("Series");
                if (seriesElement != null)
                {
                    series = seriesElement.Value;
                }
            }
        }
        else if (file.EndsWith("ComicInfo.xml"))
        {
            var document = XDocument.Load(file);
            var titleElement = document.Root.Element("Title");
            if (titleElement != null)
            {
                title = titleElement.Value;
            }
            var seriesElement = document.Root.Element("Series");
            if (seriesElement != null)
            {
                series = seriesElement.Value;
            }
        }
        return new ExtractedMetadata(series, title);
    }
}
