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

    public async Task<ChapterMergeResult> ProcessChapterMergingAsync(
        List<FoundChapter> chapters,
        string basePath,
        string outputPath,
        string seriesTitle,
        HashSet<string> existingChapterNumbers,
        Func<FoundChapter, string>? getActualFilePath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!chapters.Any())
            {
                return new ChapterMergeResult(chapters, new List<MergeInfo>());
            }

            // Determine which chapters should be merged
            Dictionary<string, List<FoundChapter>> chaptersToMerge = GroupChapterPartsForMerging(
                chapters,
                baseNumber => IsLatestChapter(baseNumber, existingChapterNumbers));

            if (!chaptersToMerge.Any())
            {
                return new ChapterMergeResult(chapters, new List<MergeInfo>());
            }

            var processedChapters = new List<FoundChapter>();
            var mergeInformation = new List<MergeInfo>();
            var processedChapterPaths = new HashSet<string>();
            var originalFilesToDelete = new List<string>();

            logger.LogInformation("Processing {GroupCount} groups of chapter parts for merging", chaptersToMerge.Count);

            // Process each group for merging
            foreach (var (baseNumber, chapterParts) in chaptersToMerge)
            {
                try
                {
                    logger.LogInformation(
                        "Merging {PartCount} chapter parts for base number {BaseNumber}",
                        chapterParts.Count, baseNumber);

                    // Create target metadata for the merged chapter
                    FoundChapter firstPart = chapterParts.First();
                    ExtractedMetadata targetMetadata = firstPart.Metadata with
                    {
                        Series = seriesTitle,
                        Number = baseNumber,
                        ChapterTitle = GenerateMergedChapterTitle(firstPart.Metadata.ChapterTitle, baseNumber)
                    };

                    // Merge the chapters
                    var (mergedChapter, originalParts) = await MergeChapterPartsAsync(
                        chapterParts,
                        basePath,
                        outputPath,
                        baseNumber,
                        targetMetadata,
                        getActualFilePath,
                        cancellationToken);

                    processedChapters.Add(mergedChapter);
                    mergeInformation.Add(new MergeInfo(mergedChapter, originalParts, baseNumber));

                    // Mark these chapter parts as processed and schedule for deletion
                    foreach (FoundChapter part in chapterParts)
                    {
                        processedChapterPaths.Add(part.RelativePath);
                        // Use the same path resolution logic that was used for reading the file
                        string actualFilePath =
                            getActualFilePath?.Invoke(part) ?? Path.Combine(basePath, part.RelativePath);
                        originalFilesToDelete.Add(actualFilePath);
                    }

                    logger.LogInformation(
                        "Successfully merged {PartCount} chapter parts into {MergedFileName}",
                        chapterParts.Count, mergedChapter.FileName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to merge chapter parts for base number {BaseNumber}. Adding original parts individually.",
                        baseNumber);

                    // If merging fails, add the original chapters individually
                    foreach (FoundChapter part in chapterParts)
                    {
                        if (!processedChapterPaths.Contains(part.RelativePath))
                        {
                            processedChapters.Add(part);
                            processedChapterPaths.Add(part.RelativePath);
                        }
                    }
                }
            }

            // Add non-merged chapters to the result
            foreach (FoundChapter chapter in chapters)
            {
                if (!processedChapterPaths.Contains(chapter.RelativePath))
                {
                    processedChapters.Add(chapter);
                }
            }

            // Delete original chapter part files after successful merging
            foreach (string filePath in originalFilesToDelete)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        logger.LogInformation("Deleted original chapter part file: {FilePath}", filePath);
                    }
                    else
                    {
                        logger.LogWarning("Original chapter part file not found for deletion: {FilePath}", filePath);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to delete original chapter part file: {FilePath}", filePath);
                }
            }

            logger.LogInformation(
                "Chapter merging completed. Processed {OriginalCount} chapters, resulted in {FinalCount} chapters with {MergeCount} merges performed",
                chapters.Count, processedChapters.Count, mergeInformation.Count);

            return new ChapterMergeResult(processedChapters, mergeInformation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during chapter merging. Falling back to original chapters.");
            return new ChapterMergeResult(chapters, new List<MergeInfo>());
        }
    }

    public async Task<ChapterMergeResult> ProcessRetroactiveMergingAsync(
        List<Chapter> existingChapters,
        string libraryPath,
        string seriesTitle,
        HashSet<string> existingChapterNumbers,
        HashSet<int> excludeMergedChapterIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!existingChapters.Any())
            {
                return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
            }

            // Convert existing chapters to FoundChapter format for processing
            List<FoundChapter> chaptersForMerging = existingChapters
                .Where(c => !excludeMergedChapterIds.Contains(c.Id))
                .Select(c => new FoundChapter(
                    c.FileName,
                    // For retroactive merging, we need just the filename since we're already in the series directory
                    // The database RelativePath is from library root (e.g., "SeriesName/Ch 21.1.cbz")
                    // But libraryPath is already the series directory, so we just need the filename
                    Path.GetFileName(c.RelativePath),
                    ChapterStorageType.Cbz,
                    new ExtractedMetadata(seriesTitle, null, ExtractChapterNumber(c.FileName))))
                .ToList();

            if (!chaptersForMerging.Any())
            {
                return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
            }

            // Find chapters that can be merged
            Dictionary<string, List<FoundChapter>> chaptersToMerge = GroupChapterPartsForMerging(
                chaptersForMerging,
                baseNumber => IsLatestChapter(baseNumber, existingChapterNumbers));

            if (!chaptersToMerge.Any())
            {
                return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
            }

            var mergeInformation = new List<MergeInfo>();

            logger.LogInformation(
                "Found {GroupCount} groups of existing chapter parts for retroactive merging",
                chaptersToMerge.Count);

            // Process each group for merging
            foreach (var (baseNumber, chapterParts) in chaptersToMerge)
            {
                try
                {
                    logger.LogInformation(
                        "Retroactively merging {PartCount} existing chapter parts for base number {BaseNumber}",
                        chapterParts.Count, baseNumber);

                    // Create target metadata for merged chapter
                    var targetMetadata = new ExtractedMetadata(
                        seriesTitle,
                        GenerateMergedChapterTitle(
                            chapterParts.First().Metadata?.ChapterTitle ??
                            Path.GetFileNameWithoutExtension(chapterParts.First().RelativePath), baseNumber),
                        baseNumber);

                    // Check if the merged file would already exist before attempting merge
                    string potentialMergedFileName = GenerateMergedFileName(chapterParts.First(), baseNumber);
                    string potentialMergedFilePath = Path.Combine(libraryPath, potentialMergedFileName);

                    if (File.Exists(potentialMergedFilePath))
                    {
                        logger.LogWarning(
                            "Skipping retroactive merge for base number {BaseNumber} because merged file {MergedFileName} already exists at {MergedFilePath}. " +
                            "This likely means the merge was already completed in a previous operation.",
                            baseNumber, potentialMergedFileName, potentialMergedFilePath);
                        continue; // Skip this merge and continue with the next one
                    }

                    // Merge the chapters
                    var (mergedChapter, originalParts) = await MergeChapterPartsAsync(
                        chapterParts,
                        libraryPath,
                        libraryPath,
                        baseNumber,
                        targetMetadata,
                        null, // For retroactive merging, paths should be correct already
                        cancellationToken);

                    mergeInformation.Add(new MergeInfo(mergedChapter, originalParts, baseNumber));

                    logger.LogInformation(
                        "Successfully merged {PartCount} existing chapter parts into {MergedFileName}",
                        chapterParts.Count, mergedChapter.FileName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to retroactively merge existing chapter parts for base number {BaseNumber}",
                        baseNumber);
                }
            }

            return new ChapterMergeResult(new List<FoundChapter>(), mergeInformation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during retroactive chapter merging");
            return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
        }
    }

    public async Task<(FoundChapter mergedChapter, List<OriginalChapterPart> originalParts)> MergeChapterPartsAsync(
        List<FoundChapter> chapterParts,
        string basePath,
        string outputPath,
        string baseChapterNumber,
        ExtractedMetadata targetMetadata,
        Func<FoundChapter, string>? getActualFilePath = null,
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
        string mergedFileName = GenerateMergedFileName(chapterParts.First(), baseChapterNumber);
        string mergedFilePath = Path.Combine(outputPath, mergedFileName);

        logger.LogInformation("Attempting to create merged file: {MergedFileName} at path: {MergedFilePath}",
            mergedFileName, mergedFilePath);

        if (File.Exists(mergedFilePath))
        {
            logger.LogWarning("Merge-target file {MergedFile} already exists at {MergedFilePath}. " +
                              "This may be from a previous merge operation. Skipping merge for safety.",
                mergedFileName, mergedFilePath);

            // Instead of throwing an exception, we should return an indication that this merge was skipped
            // For now, we'll throw but with more context about where to find the file
            throw new InvalidOperationException(
                $"Merge-target file '{mergedFileName}' already exists at path: {mergedFilePath}. " +
                $"This file likely exists from a previous merge operation. Check the library directory: {Path.GetDirectoryName(mergedFilePath)}");
        } // Create the merged CBZ file

        using (ZipArchive mergedArchive = ZipFile.Open(mergedFilePath, ZipArchiveMode.Create))
        {
            int pageCounter = 0;

            foreach (FoundChapter part in sortedParts)
            {
                // Use the custom path function if provided, otherwise use the default path construction
                string partPath = getActualFilePath?.Invoke(part) ?? Path.Combine(basePath, part.RelativePath);
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

        // Update the merged CBZ's ComicInfo.xml with the target metadata
        metadataHandling.WriteComicInfo(mergedFilePath, targetMetadata);

        var mergedChapter = new FoundChapter(
            mergedFileName,
            Path.GetRelativePath(outputPath, mergedFilePath),
            ChapterStorageType.Cbz,
            targetMetadata);

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

    private bool IsLatestChapter(string baseNumber, HashSet<string> allChapterNumbers)
    {
        if (!decimal.TryParse(baseNumber, out decimal baseNum))
        {
            return false;
        }

        // Check if there are any chapter numbers higher than this base number
        foreach (string chapterNumber in allChapterNumbers)
        {
            if (decimal.TryParse(chapterNumber, out decimal num))
            {
                decimal chapterBaseNum = Math.Floor(num);
                if (chapterBaseNum > baseNum)
                {
                    return false; // There's a higher chapter, so this isn't the latest
                }
            }
        }

        return true; // No higher chapters found, this is the latest
    }

    private string? ExtractChapterNumber(string fileName)
    {
        // Try to extract chapter number from filename
        Match match = Regex.Match(fileName,
            @"(?:Chapter\s*(?<num>\d+(?:\.\d+)?)|第(?<num>\d+(?:\.\d+)?)(?:話|章)|Kapitel\s*(?<num>\d+(?:\.\d+)?))");

        if (match.Success)
        {
            return match.Groups["num"].Value;
        }

        // Also try a simpler pattern that just looks for numbers
        Match simpleMatch = Regex.Match(fileName, @"(\d+(?:\.\d+)?)");
        if (simpleMatch.Success)
        {
            return simpleMatch.Groups[1].Value;
        }

        return null;
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