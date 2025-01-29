using System.IO.Compression;
using System.Xml.Linq;

namespace MangaIngestWithUpscaling.Services.MetadataExtraction;

public class MetadataHandlingService : IMetadataHandlingService
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

    public void WriteComicInfo(string file, ExtractedMetadata metadata)
    {
        if (file.EndsWith(".cbz"))
        {
            using var archive = ZipFile.Open(file, ZipArchiveMode.Update);
            var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
            if (comicInfoEntry != null)
            {
                using var stream = comicInfoEntry.Open();
                var document = XDocument.Load(stream);
                var titleElement = document.Root.Element("Title");
                if (titleElement != null && metadata.ChapterTitle != null)
                {
                    titleElement.Value = metadata.ChapterTitle;
                }
                var seriesElement = document.Root.Element("Series");
                if (seriesElement != null && metadata.Series != null)
                {
                    seriesElement.Value = metadata.Series;
                }
                stream.Seek(0, System.IO.SeekOrigin.Begin);
                document.Save(stream);
            }
            else
            {
                var entry = archive.CreateEntry("ComicInfo.xml");
                using var stream = entry.Open();
                var document = new XDocument(
                    new XElement("ComicInfo",
                        new XElement("Title", metadata.ChapterTitle),
                        new XElement("Series", metadata.Series)
                    )
                );
                document.Save(stream);
            }
        }
        else if (file.EndsWith("ComicInfo.xml"))
        {
            var document = XDocument.Load(file);
            var titleElement = document.Root.Element("Title");
            if (titleElement != null && metadata.ChapterTitle != null)
            {
                titleElement.Value = metadata.ChapterTitle;
            }
            var seriesElement = document.Root.Element("Series");
            if (seriesElement != null && metadata.Series != null)
            {
                seriesElement.Value = metadata.Series;
            }
            else if (metadata.Series != null)
            {
                document.Root.Add(new XElement("Series", metadata.Series));
            }
            document.Save(file);
        }
    }
}
