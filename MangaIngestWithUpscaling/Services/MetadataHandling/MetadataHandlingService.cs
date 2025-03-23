using MangaIngestWithUpscaling.Helpers;
using System.IO.Compression;
using System.Xml.Linq;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

[RegisterScoped]
public class MetadataHandlingService(
    ILogger<MetadataHandlingService> logger) : IMetadataHandlingService
{
    public ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file)
    {
        var title = string.Empty;
        var series = string.Empty;
        var number = string.Empty;
        if (file.EndsWith(".cbz"))
        {
            using var archive = ZipFile.OpenRead(file);
            var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
            if (comicInfoEntry != null)
            {
                using var stream = comicInfoEntry.Open();
                var document = XDocument.Load(stream);
                if (document.Root == null)
                {
                    return new ExtractedMetadata(series, title, number);
                }
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
                var numberElement = document.Root.Element("Number");
                if (numberElement != null)
                {
                    number = numberElement.Value;
                }
            }
        }
        else if (file.EndsWith("ComicInfo.xml"))
        {
            var document = XDocument.Load(file);
            if (document.Root == null)
            {
                return new ExtractedMetadata(series, title, number);
            }
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
            var numberElement = document.Root.Element("Number");
            if (numberElement != null)
            {
                number = numberElement.Value;
            }
        }
        return new ExtractedMetadata(series, title, number);
    }


    /// <inheritdoc/>
    public bool PagesEqual(string? file1, string? file2)
    {
        if (string.IsNullOrEmpty(file1) || string.IsNullOrEmpty(file2)) return false;

        if (!file1.EndsWith(".cbz") || !file2.EndsWith(".cbz"))
        {
            return false;
        }

        try
        {
            using var archive1 = ZipFile.OpenRead(file1);
            using var archive2 = ZipFile.OpenRead(file2);

            var files1 = archive1.Entries
                .Where(e => e.FullName.EndsWithAny("png", "jpg", "jpeg", "avif", "webp", "bmp"))
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName)) // upscaled images can have different formats
                .OrderBy(e => e)
                .ToList();

            var files2 = archive2.Entries
                .Where(e => e.FullName.EndsWithAny("png", "jpg", "jpeg", "avif", "webp", "bmp"))
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName))
                .OrderBy(e => e)
                .ToList();

            if (files1.Count != files2.Count)
            {
                return false;
            }

            return files1.SequenceEqual(files2);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError(ex, "The format of one of the following two archives is invalid.\n" +
                "Tried to compare \"{file1}\" to \"{file2}\"",
                file1, file2);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compare cbz files.\n\n" +
                "Tried to compare \"{file1}\" to \"{file2}\"",
                file1, file2);
            return false;
        }
    }

    private void WriteMetadataToXmlDoc(XDocument document, ExtractedMetadata metadata)
    {
        if (document.Root == null)
        {
            return;
        }
        if (metadata.ChapterTitle != null)
        {
            var titleElement = document.Root.Element("Title");
            if (titleElement != null && metadata.ChapterTitle != null)
            {
                titleElement.Value = metadata.ChapterTitle;
            }
            else if (metadata.ChapterTitle != null)
            {
                document.Root.Add(new XElement("Title", metadata.ChapterTitle));
            }
        }
        else
        {
            var titleElement = document.Root.Element("Title");
            if (titleElement != null)
            {
                titleElement.Remove();
            }
        }
        if (metadata.Series != null)
        {
            var seriesElement = document.Root.Element("Series");
            if (seriesElement != null && metadata.Series != null)
            {
                seriesElement.Value = metadata.Series;
            }
            else if (metadata.Series != null)
            {
                document.Root.Add(new XElement("Series", metadata.Series));
            }
        }
        else
        {
            var seriesElement = document.Root.Element("Series");
            if (seriesElement != null)
            {
                seriesElement.Remove();
            }
        }
        if (metadata.Number != null)
        {
            var numberElement = document.Root.Element("Number");
            if (numberElement != null)
            {
                numberElement.Value = metadata.Number;
            }
            else
            {
                document.Root.Add(new XElement("Number", metadata.Number));
            }
        }
        else
        {
            var numberElement = document.Root.Element("Number");
            if (numberElement != null)
            {
                numberElement.Remove();
            }
        }
    }

    public void WriteComicInfo(string file, ExtractedMetadata metadata)
    {
        metadata = metadata.CheckAndCorrect();
        if (file.EndsWith(".cbz"))
        {
            using var archive = ZipFile.Open(file, ZipArchiveMode.Update);
            var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
            if (comicInfoEntry != null)
            {
                using var stream = comicInfoEntry.Open();
                var document = XDocument.Load(stream);
                WriteMetadataToXmlDoc(document, metadata);
                stream.Seek(0, System.IO.SeekOrigin.Begin);
                document.Save(stream);
                stream.SetLength(stream.Position); // Truncate the file to the correct length
            }
            else
            {
                var entry = archive.CreateEntry("ComicInfo.xml");
                using var stream = entry.Open();
                var document = new XDocument(
                    new XElement("ComicInfo",
                        new XElement("Title", metadata.ChapterTitle),
                        new XElement("Series", metadata.Series),
                        new XElement("Number", metadata.Number),
                        new XAttribute("xmlns", "http://www.w3.org/2001/XMLSchema"),
                        new XAttribute("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance")
                    )
                );
                document.Save(stream);
                stream.SetLength(stream.Position); // Truncate the file to the correct length
            }
        }
        else if (file.EndsWith("ComicInfo.xml"))
        {
            var document = XDocument.Load(file);
            WriteMetadataToXmlDoc(document, metadata);
            document.Save(file);
        }
    }
}
