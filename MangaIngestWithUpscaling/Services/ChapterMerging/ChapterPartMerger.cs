using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public partial class ChapterPartMerger(
    IMetadataHandlingService metadataHandling,
    ILogger<ChapterPartMerger> logger) : IChapterPartMerger
{
    public Dictionary<string, List<FoundChapter>> GroupChapterPartsForMerging(
        IEnumerable<FoundChapter> chapters,
        Func<string, bool> isLastChapter)
    {
        var result = new Dictionary<string, List<FoundChapter>>();

        foreach (FoundChapter chapter in chapters)
        {
            string? chapterNumber = ExtractChapterNumber(chapter);
            if (chapterNumber == null)
            {
                continue;
            }

            string? baseNumber = ExtractBaseChapterNumber(chapterNumber);
            if (baseNumber == null)
            {
                continue;
            }

            // Don't merge if this is the latest chapter
            if (isLastChapter(baseNumber))
            {
                continue;
            }

            if (!result.ContainsKey(baseNumber))
            {
                result[baseNumber] = new List<FoundChapter>();
            }

            result[baseNumber].Add(chapter);
        }

        // Only return groups that have more than one part AND have consecutive decimal steps
        return result.Where(kvp => kvp.Value.Count > 1 && AreConsecutiveChapterParts(kvp.Value, kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public async Task<(FoundChapter mergedChapter, List<OriginalChapterPart> originalParts)> MergeChapterPartsAsync(
        List<FoundChapter> chapterParts,
        string basePath,
        string outputPath,
        string baseChapterNumber,
        CancellationToken cancellationToken = default)
    {
        if (!chapterParts.Any())
        {
            throw new ArgumentException("No chapter parts provided", nameof(chapterParts));
        }

        // Sort chapter parts by their part number to ensure correct order
        List<FoundChapter> sortedParts = chapterParts
            .Select(c => new { Chapter = c, PartNumber = ExtractPartNumber(ExtractChapterNumber(c)) })
            .Where(x => x.PartNumber.HasValue)
            .OrderBy(x => x.PartNumber)
            .Select(x => x.Chapter)
            .ToList();

        if (!sortedParts.Any())
        {
            logger.LogWarning("No valid chapter parts found for merging");
            throw new ArgumentException("No valid chapter parts found", nameof(chapterParts));
        }

        var originalParts = new List<OriginalChapterPart>();
        string mergedFileName = GenerateMergedFileName(sortedParts.First(), baseChapterNumber);
        string mergedFilePath = Path.Combine(outputPath, mergedFileName);

        if (File.Exists(mergedFilePath))
        {
            logger.LogWarning("Merge-target file {MergedFile} already exists, skipping merge", mergedFileName);
            throw new InvalidOperationException($"Merge-target file '{mergedFileName}' already exists.");
        }

        // Create the merged CBZ file
        using (ZipArchive mergedArchive = ZipFile.Open(mergedFilePath, ZipArchiveMode.Create))
        {
            int pageCounter = 0;

            foreach (FoundChapter part in sortedParts)
            {
                string partPath = Path.Combine(basePath, part.RelativePath);
                var pageNames = new List<string>();
                int startPageIndex = pageCounter;

                // Read pages from the part's CBZ file
                using (ZipArchive partArchive = ZipFile.OpenRead(partPath))
                {
                    // Get image entries, sorted by name for consistent ordering
                    List<ZipArchiveEntry> imageEntries = partArchive.Entries
                        .Where(e => IsImageFile(e.Name) && !e.FullName.EndsWith("/"))
                        .OrderBy(e => e.Name, new NaturalStringComparer())
                        .ToList();

                    foreach (ZipArchiveEntry entry in imageEntries)
                    {
                        // Generate new page name with proper padding
                        string newPageName = $"{pageCounter:D4}{Path.GetExtension(entry.Name)}";
                        pageNames.Add(entry.Name); // Store original name

                        // Copy the image to the merged archive
                        ZipArchiveEntry newEntry = mergedArchive.CreateEntry(newPageName);
                        using (Stream originalStream = entry.Open())
                        using (Stream newStream = newEntry.Open())
                        {
                            await originalStream.CopyToAsync(newStream, cancellationToken);
                        }

                        pageCounter++;
                    }

                    // Copy ComicInfo.xml if it exists (we'll update it later)
                    ZipArchiveEntry? comicInfoEntry = partArchive.GetEntry("ComicInfo.xml");
                    if (comicInfoEntry != null && originalParts.Count == 0) // Only copy from first part
                    {
                        ZipArchiveEntry newComicInfo = mergedArchive.CreateEntry("ComicInfo.xml");
                        using (Stream originalStream = comicInfoEntry.Open())
                        using (Stream newStream = newComicInfo.Open())
                        {
                            await originalStream.CopyToAsync(newStream, cancellationToken);
                        }
                    }
                }

                // Store original part information
                string chapterNumber = ExtractChapterNumber(part) ?? baseChapterNumber;
                originalParts.Add(new OriginalChapterPart
                {
                    FileName = part.FileName,
                    ChapterNumber = chapterNumber,
                    Metadata = part.Metadata,
                    PageNames = pageNames,
                    StartPageIndex = startPageIndex,
                    EndPageIndex = pageCounter - 1
                });
            }
        }

        // Update the merged CBZ's ComicInfo.xml with the base chapter number
        FoundChapter firstPart = sortedParts.First();
        ExtractedMetadata mergedMetadata = firstPart.Metadata with
        {
            ChapterTitle = GenerateMergedChapterTitle(firstPart.Metadata.ChapterTitle, baseChapterNumber),
            Number = baseChapterNumber
        };

        metadataHandling.WriteComicInfo(mergedFilePath, mergedMetadata);

        var mergedChapter = new FoundChapter(
            mergedFileName,
            Path.GetRelativePath(outputPath, mergedFilePath),
            ChapterStorageType.Cbz,
            mergedMetadata);

        logger.LogInformation("Merged {PartCount} chapter parts into {MergedFile}",
            sortedParts.Count, mergedFileName);

        return (mergedChapter, originalParts);
    }

    public async Task<List<FoundChapter>> RestoreChapterPartsAsync(
        string mergedChapterPath,
        List<OriginalChapterPart> originalParts,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var restoredChapters = new List<FoundChapter>();

        using (ZipArchive mergedArchive = ZipFile.OpenRead(mergedChapterPath))
        {
            foreach (OriginalChapterPart originalPart in originalParts)
            {
                string partPath = Path.Combine(outputDirectory, originalPart.FileName);

                // Create the individual part CBZ
                using (ZipArchive partArchive = ZipFile.Open(partPath, ZipArchiveMode.Create))
                {
                    // Extract pages for this part
                    for (int i = originalPart.StartPageIndex; i <= originalPart.EndPageIndex; i++)
                    {
                        string paddedIndex = $"{i:D4}";
                        ZipArchiveEntry? mergedEntry = mergedArchive.Entries
                            .FirstOrDefault(e => e.Name.StartsWith(paddedIndex));

                        if (mergedEntry != null)
                        {
                            // Restore original page name if available
                            string originalPageName = i - originalPart.StartPageIndex < originalPart.PageNames.Count
                                ? originalPart.PageNames[i - originalPart.StartPageIndex]
                                : mergedEntry.Name;

                            ZipArchiveEntry partEntry = partArchive.CreateEntry(originalPageName);
                            using (Stream mergedStream = mergedEntry.Open())
                            using (Stream partStream = partEntry.Open())
                            {
                                await mergedStream.CopyToAsync(partStream, cancellationToken);
                            }
                        }
                    }

                    // Create ComicInfo.xml with original metadata
                    ZipArchiveEntry comicInfoEntry = partArchive.CreateEntry("ComicInfo.xml");
                    using (Stream stream = comicInfoEntry.Open())
                    {
                        metadataHandling.WriteComicInfo(partPath, originalPart.Metadata);
                    }
                }

                restoredChapters.Add(new FoundChapter(
                    originalPart.FileName,
                    Path.GetRelativePath(outputDirectory, partPath),
                    ChapterStorageType.Cbz,
                    originalPart.Metadata));
            }
        }

        logger.LogInformation("Restored {PartCount} chapter parts from {MergedFile}",
            originalParts.Count, Path.GetFileName(mergedChapterPath));

        return restoredChapters;
    }

    private string? ExtractChapterNumber(FoundChapter chapter)
    {
        // Try to extract from metadata first
        if (!string.IsNullOrEmpty(chapter.Metadata.Number))
        {
            return chapter.Metadata.Number;
        }

        // Try to extract from chapter title
        if (!string.IsNullOrEmpty(chapter.Metadata.ChapterTitle))
        {
            Match match = ChapterNumberRegex().Match(chapter.Metadata.ChapterTitle);
            if (match.Success)
            {
                string num = match.Groups["num"].Value;
                string subnum = match.Groups["subnum"].Value;
                return string.IsNullOrEmpty(subnum) ? num : $"{num}.{subnum}";
            }
        }

        // Try to extract from filename
        Match fileMatch = ChapterNumberRegex().Match(chapter.FileName);
        if (fileMatch.Success)
        {
            string num = fileMatch.Groups["num"].Value;
            string subnum = fileMatch.Groups["subnum"].Value;
            return string.IsNullOrEmpty(subnum) ? num : $"{num}.{subnum}";
        }

        return null;
    }

    private string? ExtractBaseChapterNumber(string chapterNumber)
    {
        if (decimal.TryParse(chapterNumber, out decimal number))
        {
            return Math.Floor(number).ToString();
        }

        // Handle cases like "22.1" -> "22"
        int dotIndex = chapterNumber.IndexOf('.');
        if (dotIndex > 0)
        {
            return chapterNumber.Substring(0, dotIndex);
        }

        return chapterNumber;
    }

    private decimal? ExtractPartNumber(string? chapterNumber)
    {
        if (string.IsNullOrEmpty(chapterNumber))
        {
            return null;
        }

        if (decimal.TryParse(chapterNumber, out decimal number))
        {
            return number;
        }

        return null;
    }

    private string GenerateMergedFileName(FoundChapter firstPart, string baseChapterNumber)
    {
        string extension = Path.GetExtension(firstPart.FileName);
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(firstPart.FileName);

        // Replace the chapter number in the filename with the base number
        string baseFileName = ChapterNumberRegex().Replace(nameWithoutExtension,
            match => match.Value.Replace(match.Groups["num"].Value +
                                         (string.IsNullOrEmpty(match.Groups["subnum"].Value)
                                             ? ""
                                             : "." + match.Groups["subnum"].Value),
                baseChapterNumber));

        return $"{baseFileName}{extension}";
    }

    private string? GenerateMergedChapterTitle(string? originalTitle, string baseChapterNumber)
    {
        if (string.IsNullOrEmpty(originalTitle))
        {
            return null;
        }

        // Replace the chapter number in the title with the base number
        return ChapterNumberRegex().Replace(originalTitle,
            match => match.Value.Replace(match.Groups["num"].Value +
                                         (string.IsNullOrEmpty(match.Groups["subnum"].Value)
                                             ? ""
                                             : "." + match.Groups["subnum"].Value),
                baseChapterNumber));
    }

    /// <summary>
    ///     Validates that the chapters represent consecutive parts with 0.1 increments
    ///     Supports flexible sequences that can start with base number or .1
    ///     Valid examples:
    ///     - 22.1, 22.2, 22.3 ✓ (consecutive 0.1 increments)
    ///     - 22, 22.1, 22.2 ✓ (base followed by consecutive parts)
    ///     - 22, 22.2 ✓ (special case: base + .2)
    ///     - 22, 22.2, 22.3, 22.4 ✓ (special case continuing as regular sequence)
    ///     - 22.1, 22.2 ✓ (consecutive pair)
    ///     Invalid examples:
    ///     - 22.1, 22.3 ✗ (missing 22.2)
    ///     - 5, 5.5 ✗ (5.5 is special chapter, not part)
    ///     - 22, 22.3 ✗ (gap from base to .3)
    /// </summary>
    private bool AreConsecutiveChapterParts(List<FoundChapter> chapters, string baseNumber)
    {
        if (chapters.Count < 2)
        {
            return false;
        }

        // Extract and sort the decimal parts
        List<decimal> chapterNumbers = chapters
            .Select(c => ExtractChapterNumber(c))
            .Where(n => n != null)
            .Select(n => decimal.TryParse(n, out decimal num) ? num : (decimal?)null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .OrderBy(n => n)
            .ToList();

        if (chapterNumbers.Count != chapters.Count)
        {
            return false;
        }

        if (!decimal.TryParse(baseNumber, out decimal baseNum))
        {
            return false;
        }

        // Check if we have the base number
        bool hasBaseNumber = chapterNumbers.Contains(baseNum);

        // Analyze the sequence pattern
        for (int i = 0; i < chapterNumbers.Count; i++)
        {
            decimal currentNumber = chapterNumbers[i];

            if (currentNumber == baseNum)
            {
                // Base number must be first in sequence
                if (i != 0)
                {
                    return false;
                }

                // Next number after base can be .1 or .2 (special case)
                if (i + 1 < chapterNumbers.Count)
                {
                    decimal nextNumber = chapterNumbers[i + 1];
                    if (nextNumber != baseNum + 0.1m && nextNumber != baseNum + 0.2m)
                    {
                        return false; // Invalid jump from base
                    }
                }
            }
            else
            {
                // For decimal parts, determine expected value based on position and context
                decimal expectedValue;

                if (hasBaseNumber)
                {
                    // Sequence includes base number
                    if (i == 1)
                    {
                        // First decimal after base can be .1 or .2
                        expectedValue = chapterNumbers[1]; // Accept whatever comes after base

                        // But validate it's a reasonable decimal part (.1, .2, etc.)
                        decimal decimalPart = expectedValue - baseNum;
                        if (decimalPart != 0.1m && decimalPart != 0.2m)
                        {
                            return false; // Invalid decimal part after base
                        }
                    }
                    else
                    {
                        // Subsequent decimals must increment by 0.1
                        expectedValue = chapterNumbers[i - 1] + 0.1m;
                    }
                }
                else
                {
                    // Sequence starts with decimal (e.g., 22.1, 22.2, 22.3)
                    if (i == 0)
                    {
                        // First number should be base + 0.1
                        expectedValue = baseNum + 0.1m;
                    }
                    else
                    {
                        // Subsequent numbers increment by 0.1
                        expectedValue = chapterNumbers[i - 1] + 0.1m;
                    }
                }

                // Check if current number matches expected (with floating point tolerance)
                if (Math.Abs(currentNumber - expectedValue) > 0.001m)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsImageFile(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".avif";
    }

    [GeneratedRegex(
        @"
               # Group 1: Latin, Cyrillic & similar alphabets
               (?:
                 # Core English, German, Russian
                 ch(?:apter)?\.? | kapitel | glava | file | ep(?:isode|\.)?

                 # Romance Languages
                 | cap(?:[íi]tulo|itol|\.)? | chap(?:itre|\.)?

                 # Eastern European Languages
                 | rozdział | kapitola | fejezet | poglavlje | розділ

                 # South-East Asian Languages (Latin script)
                 | chương | bab | kabanata
               )
               \s*(?<num>\d+)(?:[.-](?<subnum>\d+))?


               # Group 2: CJK (Chinese, Japanese, Korean)
               | (?:第|제)
               \s*(?<num>\d+)(?:[.-](?<subnum>\d+))?
               \s*(?:話|章|话|화|장)?


               # Group 3: Other SEA Scripts (Thai, Burmese)
               | (?:บทที่|အခန်း)
               \s*(?<num>\d+)(?:[.-](?<subnum>\d+))?
               ",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex ChapterNumberRegex();
}

/// <summary>
///     Natural string comparer for proper numeric sorting (e.g., "page2.jpg" before "page10.jpg")
/// </summary>
public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null)
        {
            return 0;
        }

        if (x == null)
        {
            return -1;
        }

        if (y == null)
        {
            return 1;
        }

        List<object> xParts = GetParts(x);
        List<object> yParts = GetParts(y);

        int minLength = Math.Min(xParts.Count, yParts.Count);

        for (int i = 0; i < minLength; i++)
        {
            int result = CompareParts(xParts[i], yParts[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return xParts.Count.CompareTo(yParts.Count);
    }

    private static List<object> GetParts(string str)
    {
        var parts = new List<object>();
        string current = "";
        bool isNumber = false;

        foreach (char c in str)
        {
            bool charIsNumber = char.IsDigit(c);

            if (charIsNumber != isNumber)
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(isNumber ? int.Parse(current) : current);
                }

                current = c.ToString();
                isNumber = charIsNumber;
            }
            else
            {
                current += c;
            }
        }

        if (!string.IsNullOrEmpty(current))
        {
            parts.Add(isNumber ? int.Parse(current) : current);
        }

        return parts;
    }

    private static int CompareParts(object x, object y)
    {
        if (x is int xInt && y is int yInt)
        {
            return xInt.CompareTo(yInt);
        }

        return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
    }
}