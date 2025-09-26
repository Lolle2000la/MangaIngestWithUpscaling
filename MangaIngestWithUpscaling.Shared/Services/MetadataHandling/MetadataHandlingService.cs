using System.IO.Compression;
using System.Xml.Linq;
using MangaIngestWithUpscaling.Shared.Constants;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

[RegisterScoped]
public class MetadataHandlingService(ILogger<MetadataHandlingService> logger)
    : IMetadataHandlingService
{
    public async Task<ExtractedMetadata> GetSeriesAndTitleFromComicInfoAsync(string file)
    {
        var title = string.Empty;
        var series = string.Empty;
        var number = string.Empty;
        if (file.EndsWith(".cbz"))
        {
            await using ZipArchive archive = await ZipFile.OpenReadAsync(file);
            var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
            if (comicInfoEntry != null)
            {
                await using Stream stream = await comicInfoEntry.OpenAsync();
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
    public async Task<bool> PagesEqualAsync(string? file1, string? file2)
    {
        if (string.IsNullOrEmpty(file1) || string.IsNullOrEmpty(file2))
            return false;

        if (!file1.EndsWith(".cbz") || !file2.EndsWith(".cbz"))
        {
            return false;
        }

        try
        {
            await using ZipArchive archive1 = await ZipFile.OpenReadAsync(file1);
            await using ZipArchive archive2 = await ZipFile.OpenReadAsync(file2);

            var files1 = archive1
                .Entries.Where(e =>
                    ImageConstants.IsSupportedImageExtension(
                        Path.GetExtension(e.FullName).ToLowerInvariant()
                    )
                )
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName)) // upscaled images can have different formats
                .OrderBy(e => e)
                .ToList();

            var files2 = archive2
                .Entries.Where(e =>
                    ImageConstants.IsSupportedImageExtension(
                        Path.GetExtension(e.FullName).ToLowerInvariant()
                    )
                )
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
            logger.LogError(
                ex,
                "The format of one of the following two archives is invalid.\n"
                    + "Tried to compare \"{file1}\" to \"{file2}\"",
                file1,
                file2
            );
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to compare cbz files.\n\n" + "Tried to compare \"{file1}\" to \"{file2}\"",
                file1,
                file2
            );
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<PageDifferenceResult> AnalyzePageDifferencesAsync(
        string? originalFile,
        string? upscaledFile
    )
    {
        if (string.IsNullOrEmpty(originalFile) || string.IsNullOrEmpty(upscaledFile))
            return new PageDifferenceResult([], []);

        if (!originalFile.EndsWith(".cbz") || !upscaledFile.EndsWith(".cbz"))
        {
            return new PageDifferenceResult([], []);
        }

        try
        {
            await using ZipArchive originalArchive = await ZipFile.OpenReadAsync(originalFile);
            await using ZipArchive upscaledArchive = await ZipFile.OpenReadAsync(upscaledFile);

            var originalPages = originalArchive
                .Entries.Where(e =>
                    ImageConstants.IsSupportedImageExtension(
                        Path.GetExtension(e.FullName).ToLowerInvariant()
                    )
                )
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName))
                .OrderBy(e => e)
                .ToList();

            var upscaledPages = upscaledArchive
                .Entries.Where(e =>
                    ImageConstants.IsSupportedImageExtension(
                        Path.GetExtension(e.FullName).ToLowerInvariant()
                    )
                )
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName))
                .OrderBy(e => e)
                .ToList();

            var missingPages = originalPages.Except(upscaledPages).ToList();
            var extraPages = upscaledPages.Except(originalPages).ToList();

            return new PageDifferenceResult(missingPages, extraPages);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError(
                ex,
                "The format of one of the following two archives is invalid.\n"
                    + "Tried to analyze differences between \"{originalFile}\" and \"{upscaledFile}\"",
                originalFile,
                upscaledFile
            );
            return new PageDifferenceResult([], []);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to analyze cbz file differences.\n\n"
                    + "Tried to analyze differences between \"{originalFile}\" and \"{upscaledFile}\"",
                originalFile,
                upscaledFile
            );
            return new PageDifferenceResult([], []);
        }
    }

    public async Task WriteComicInfoAsync(string file, ExtractedMetadata metadata)
    {
        metadata = metadata.CheckAndCorrect();
        if (file.EndsWith(".cbz"))
        {
            await using ZipArchive archive = await ZipFile.OpenAsync(file, ZipArchiveMode.Update);
            await WriteComicInfoAsync(archive, metadata);
        }
        else if (file.EndsWith("ComicInfo.xml"))
        {
            var document = XDocument.Load(file);
            WriteMetadataToXmlDoc(document, metadata);
            document.Save(file);
        }
    }

    /// <summary>
    ///     Writes ComicInfo.xml metadata directly to an existing ZipArchive
    /// </summary>
    /// <param name="archive">The ZIP archive to write to</param>
    /// <param name="metadata">The metadata to write</param>
    public async Task WriteComicInfoAsync(ZipArchive archive, ExtractedMetadata metadata)
    {
        metadata = metadata.CheckAndCorrect();

        // Check if we can update existing entries (Update mode) or only create new ones (Create mode)
        ZipArchiveEntry? comicInfoEntry =
            archive.Mode != ZipArchiveMode.Create ? archive.GetEntry("ComicInfo.xml") : null;

        if (comicInfoEntry != null)
        {
            // Updating existing ComicInfo.xml in Update mode
            await using Stream stream = await comicInfoEntry.OpenAsync();
            XDocument document = XDocument.Load(stream);
            WriteMetadataToXmlDoc(document, metadata);
            stream.Seek(0, SeekOrigin.Begin);
            document.Save(stream);
            stream.SetLength(stream.Position); // Truncate - only works in Update mode
        }
        else
        {
            // Creating new ComicInfo.xml (works in both Create and Update modes)
            ZipArchiveEntry entry = archive.CreateEntry("ComicInfo.xml");
            await using Stream stream = await entry.OpenAsync();
            var document = new XDocument(
                new XElement(
                    "ComicInfo",
                    new XElement("Title", metadata.ChapterTitle),
                    new XElement("Series", metadata.Series),
                    new XElement("Number", metadata.Number)
                )
            );
            document.Save(stream);
            // No SetLength needed when creating new entries
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
            XElement? titleElement = document.Root.Element("Title");
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
            XElement? titleElement = document.Root.Element("Title");
            if (titleElement != null)
            {
                titleElement.Remove();
            }
        }

        if (metadata.Series != null)
        {
            XElement? seriesElement = document.Root.Element("Series");
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
            XElement? seriesElement = document.Root.Element("Series");
            if (seriesElement != null)
            {
                seriesElement.Remove();
            }
        }

        if (metadata.Number != null)
        {
            XElement? numberElement = document.Root.Element("Number");
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
            XElement? numberElement = document.Root.Element("Number");
            if (numberElement != null)
            {
                numberElement.Remove();
            }
        }
    }
}
