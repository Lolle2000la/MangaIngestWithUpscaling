using System.IO.Compression;
using System.Text.RegularExpressions;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public partial class ChapterPartMerger(
    IMetadataHandlingService metadataHandling,
    IStringLocalizer<ChapterPartMerger> localizer,
    ILogger<ChapterPartMerger> logger
) : IChapterPartMerger
{
    private const decimal ChapterPartIncrement = 0.1m;
    private const decimal DecimalComparisonTolerance = 0.001m;

    public Dictionary<string, List<FoundChapter>> GroupChapterPartsForMerging(
        IEnumerable<FoundChapter> chapters,
        Func<string, bool> isLastChapter
    )
    {
        var result = new Dictionary<string, List<FoundChapter>>();

        logger.LogDebug(
            "GroupChapterPartsForMerging: Processing {ChapterCount} chapters",
            chapters.Count()
        );

        foreach (FoundChapter chapter in chapters)
        {
            string? chapterNumber = ExtractChapterNumber(chapter);
            if (chapterNumber == null)
            {
                logger.LogDebug(
                    "GroupChapterPartsForMerging: Skipping {FileName} - no chapter number extracted",
                    chapter.FileName
                );
                continue;
            }

            string? baseNumber = ExtractBaseChapterNumber(chapterNumber);
            if (baseNumber == null)
            {
                logger.LogDebug(
                    "GroupChapterPartsForMerging: Skipping {FileName} (chapter {ChapterNumber}) - no base number extracted",
                    chapter.FileName,
                    chapterNumber
                );
                continue;
            }

            // Don't merge if this is the latest chapter
            if (isLastChapter(baseNumber))
            {
                logger.LogDebug(
                    "GroupChapterPartsForMerging: Skipping {FileName} (chapter {ChapterNumber}, base {BaseNumber}) - is latest chapter",
                    chapter.FileName,
                    chapterNumber,
                    baseNumber
                );
                continue;
            }

            if (!result.ContainsKey(baseNumber))
            {
                result[baseNumber] = new List<FoundChapter>();
            }

            result[baseNumber].Add(chapter);
            logger.LogDebug(
                "GroupChapterPartsForMerging: Added {FileName} (chapter {ChapterNumber}) to base group {BaseNumber}",
                chapter.FileName,
                chapterNumber,
                baseNumber
            );
        }

        // Only return groups that have more than one part AND have consecutive decimal steps
        // For groups with gaps, extract only the consecutive sequence from the beginning
        var filteredResult = new Dictionary<string, List<FoundChapter>>();

        foreach (var kvp in result.Where(kvp => kvp.Value.Count > 1))
        {
            List<FoundChapter> consecutiveChapters = GetConsecutiveChapterParts(kvp.Value, kvp.Key);
            if (consecutiveChapters.Count > 1)
            {
                filteredResult[kvp.Key] = consecutiveChapters;
                if (consecutiveChapters.Count < kvp.Value.Count)
                {
                    logger.LogDebug(
                        "GroupChapterPartsForMerging: Found {ConsecutiveCount} consecutive chapters out of {TotalCount} for base {BaseNumber}",
                        consecutiveChapters.Count,
                        kvp.Value.Count,
                        kvp.Key
                    );
                }
            }
        }

        logger.LogDebug(
            "GroupChapterPartsForMerging: Initial groups: {InitialCount}, After filtering: {FilteredCount}. Groups: [{Groups}]",
            result.Count,
            filteredResult.Count,
            string.Join(", ", filteredResult.Keys)
        );

        return filteredResult;
    }

    /// <summary>
    /// Groups chapters that can be added to existing merged chapters.
    /// This handles individual chapters (like 2.3) that can be added to existing merged chapters (like merged chapter 2).
    /// Only chapters that would form a consecutive sequence with the already-merged parts are included.
    /// </summary>
    /// <param name="chapters">Chapters to analyze</param>
    /// <param name="existingMergedBaseNumbers">Base numbers of existing merged chapters</param>
    /// <param name="existingMergedParts">Dictionary mapping base numbers to lists of already-merged chapter numbers (e.g., "1" -> ["1.1", "1.2"])</param>
    /// <param name="isLastChapter">Function to determine if a chapter group is the latest chapter</param>
    /// <returns>Dictionary where key is base chapter number and value is list of chapters to add to existing merged chapter</returns>
    public Dictionary<string, List<FoundChapter>> GroupChaptersForAdditionToExistingMerged(
        IEnumerable<FoundChapter> chapters,
        HashSet<string> existingMergedBaseNumbers,
        Dictionary<string, List<string>> existingMergedParts,
        Func<string, bool> isLastChapter
    )
    {
        var result = new Dictionary<string, List<FoundChapter>>();

        logger.LogDebug(
            "GroupChaptersForAdditionToExistingMerged: Processing {ChapterCount} chapters, {ExistingMergedCount} existing merged base numbers: [{ExistingMerged}]",
            chapters.Count(),
            existingMergedBaseNumbers.Count,
            string.Join(", ", existingMergedBaseNumbers)
        );

        // Group candidate chapters by base number first
        var candidatesByBase = new Dictionary<string, List<FoundChapter>>();

        foreach (FoundChapter chapter in chapters)
        {
            string? chapterNumber = ExtractChapterNumber(chapter);
            if (chapterNumber == null)
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Skipping {FileName} - no chapter number extracted",
                    chapter.FileName
                );
                continue;
            }

            string? baseNumber = ExtractBaseChapterNumber(chapterNumber);
            if (baseNumber == null)
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Skipping {FileName} (chapter {ChapterNumber}) - no base number extracted",
                    chapter.FileName,
                    chapterNumber
                );
                continue;
            }

            // Only consider chapters whose base number matches an existing merged chapter
            if (!existingMergedBaseNumbers.Contains(baseNumber))
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Skipping {FileName} (chapter {ChapterNumber}, base {BaseNumber}) - base number not in existing merged chapters",
                    chapter.FileName,
                    chapterNumber,
                    baseNumber
                );
                continue;
            }

            // Don't merge if this is the latest chapter
            if (isLastChapter(baseNumber))
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Skipping {FileName} (chapter {ChapterNumber}, base {BaseNumber}) - is latest chapter",
                    chapter.FileName,
                    chapterNumber,
                    baseNumber
                );
                continue;
            }

            // Validate that this is a proper chapter part (has decimal part)
            if (!IsChapterPart(chapterNumber, baseNumber))
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Skipping {FileName} (chapter {ChapterNumber}, base {BaseNumber}) - not a valid chapter part",
                    chapter.FileName,
                    chapterNumber,
                    baseNumber
                );
                continue;
            }

            if (!candidatesByBase.ContainsKey(baseNumber))
            {
                candidatesByBase[baseNumber] = new List<FoundChapter>();
            }

            candidatesByBase[baseNumber].Add(chapter);
            logger.LogDebug(
                "GroupChaptersForAdditionToExistingMerged: Added candidate {FileName} (chapter {ChapterNumber}) for base group {BaseNumber}",
                chapter.FileName,
                chapterNumber,
                baseNumber
            );
        }

        // Now validate consecutive sequences for each base number
        foreach (var (baseNumber, candidates) in candidatesByBase)
        {
            // Get the existing merged parts for this base number
            if (!existingMergedParts.TryGetValue(baseNumber, out List<string>? mergedParts))
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: No existing merged parts info for base {BaseNumber}, skipping validation",
                    baseNumber
                );
                continue;
            }

            // Combine existing parts with candidates to check for consecutive sequence
            var allChapters = mergedParts
                .Select(num => CreateFoundChapter($"Chapter {num}.cbz", num))
                .Concat(candidates)
                .ToList();

            // Use GetConsecutiveChapterParts to find the consecutive sequence
            List<FoundChapter> consecutiveChapters = GetConsecutiveChapterParts(
                allChapters,
                baseNumber
            );

            // Find which candidates are in the consecutive sequence
            var consecutiveCandidates = candidates
                .Where(c =>
                    consecutiveChapters.Any(cc =>
                        cc.FileName == c.FileName || cc.Metadata.Number == c.Metadata.Number
                    )
                )
                .ToList();

            if (consecutiveCandidates.Any())
            {
                result[baseNumber] = consecutiveCandidates;
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Will add {Count} consecutive chapters to existing merged base {BaseNumber}",
                    consecutiveCandidates.Count,
                    baseNumber
                );
            }
            else if (candidates.Any())
            {
                logger.LogDebug(
                    "GroupChaptersForAdditionToExistingMerged: Rejected {Count} candidates for base {BaseNumber} due to gaps in sequence",
                    candidates.Count,
                    baseNumber
                );
            }
        }

        logger.LogDebug(
            "GroupChaptersForAdditionToExistingMerged: Found {GroupCount} groups for addition to existing merged chapters: [{Groups}]",
            result.Count,
            string.Join(", ", result.Keys)
        );

        return result;
    }

    public async Task<ChapterMergeResult> ProcessChapterMergingAsync(
        List<FoundChapter> chapters,
        string basePath,
        string outputPath,
        string seriesTitle,
        HashSet<string> existingChapterNumbers,
        Func<FoundChapter, string>? getActualFilePath = null,
        CancellationToken cancellationToken = default
    )
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
                baseNumber => IsLatestChapter(baseNumber, existingChapterNumbers)
            );

            if (!chaptersToMerge.Any())
            {
                return new ChapterMergeResult(chapters, new List<MergeInfo>());
            }

            var processedChapters = new List<FoundChapter>();
            var mergeInformation = new List<MergeInfo>();
            var processedChapterPaths = new HashSet<string>();
            var originalFilesToDelete = new List<string>();

            logger.LogInformation(
                "Processing {GroupCount} groups of chapter parts for merging",
                chaptersToMerge.Count
            );

            // Process each group for merging
            foreach (var (baseNumber, chapterParts) in chaptersToMerge)
            {
                try
                {
                    logger.LogInformation(
                        "Merging {PartCount} chapter parts for base number {BaseNumber}",
                        chapterParts.Count,
                        baseNumber
                    );

                    // Create target metadata for the merged chapter
                    FoundChapter firstPart = chapterParts.First();
                    ExtractedMetadata targetMetadata = firstPart.Metadata with
                    {
                        Series = seriesTitle,
                        Number = baseNumber,
                        ChapterTitle = GenerateMergedChapterTitle(
                            firstPart.Metadata.ChapterTitle,
                            baseNumber
                        ),
                    };

                    // Merge the chapters
                    var (mergedChapter, originalParts) = await MergeChapterPartsAsync(
                        chapterParts,
                        basePath,
                        outputPath,
                        baseNumber,
                        targetMetadata,
                        getActualFilePath,
                        cancellationToken
                    );

                    processedChapters.Add(mergedChapter);
                    mergeInformation.Add(new MergeInfo(mergedChapter, originalParts, baseNumber));

                    // Mark these chapter parts as processed and schedule for deletion
                    foreach (FoundChapter part in chapterParts)
                    {
                        processedChapterPaths.Add(part.RelativePath);
                        // Use the same path resolution logic that was used for reading the file
                        string actualFilePath =
                            getActualFilePath?.Invoke(part)
                            ?? Path.Combine(basePath, part.RelativePath);
                        originalFilesToDelete.Add(actualFilePath);
                    }

                    logger.LogInformation(
                        "Successfully merged {PartCount} chapter parts into {MergedFileName}",
                        chapterParts.Count,
                        mergedChapter.FileName
                    );
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to merge chapter parts for base number {BaseNumber}. Adding original parts individually.",
                        baseNumber
                    );

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
                        logger.LogInformation(
                            "Deleted original chapter part file: {FilePath}",
                            filePath
                        );
                    }
                    else
                    {
                        logger.LogWarning(
                            "Original chapter part file not found for deletion: {FilePath}",
                            filePath
                        );
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to delete original chapter part file: {FilePath}",
                        filePath
                    );
                }
            }

            logger.LogInformation(
                "Chapter merging completed. Processed {OriginalCount} chapters, resulted in {FinalCount} chapters with {MergeCount} merges performed",
                chapters.Count,
                processedChapters.Count,
                mergeInformation.Count
            );

            return new ChapterMergeResult(processedChapters, mergeInformation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during chapter merging. Falling back to original chapters.");
            return new ChapterMergeResult(chapters, new List<MergeInfo>());
        }
    }

    public async Task<ChapterMergeResult> ProcessExistingChapterPartsAsync(
        List<Chapter> existingChapters,
        string libraryPath,
        string seriesTitle,
        HashSet<string> existingChapterNumbers,
        HashSet<int> excludeMergedChapterIds,
        HashSet<string> existingMergedBaseNumbers,
        Dictionary<string, List<string>> existingMergedParts,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (!existingChapters.Any())
            {
                return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
            }

            // Calculate the series directory path within the library
            string seriesDirectoryPath = Path.Combine(
                libraryPath,
                PathEscaper.EscapeFileName(seriesTitle)
            );

            // Convert existing chapters to FoundChapter format for processing
            List<FoundChapter> chaptersForMerging = existingChapters
                .Where(c => !excludeMergedChapterIds.Contains(c.Id))
                .Select(c => new FoundChapter(
                    c.FileName,
                    // For existing chapter merging, use just the filename since chapters are in the series directory
                    Path.GetFileName(c.RelativePath),
                    ChapterStorageType.Cbz,
                    new ExtractedMetadata(seriesTitle, null, ExtractChapterNumber(c.FileName))
                ))
                .ToList();

            if (!chaptersForMerging.Any())
            {
                return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
            }

            // Find chapters that can be merged into new merge groups
            Dictionary<string, List<FoundChapter>> chaptersToMerge = GroupChapterPartsForMerging(
                chaptersForMerging,
                baseNumber => IsLatestChapter(baseNumber, existingChapterNumbers)
            );

            // Find individual chapters that can be added to existing merged chapters
            Dictionary<string, List<FoundChapter>> chaptersToAddToExisting =
                GroupChaptersForAdditionToExistingMerged(
                    chaptersForMerging,
                    existingMergedBaseNumbers,
                    existingMergedParts,
                    baseNumber => IsLatestChapter(baseNumber, existingChapterNumbers)
                );

            if (!chaptersToMerge.Any() && !chaptersToAddToExisting.Any())
            {
                return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
            }

            var mergeInformation = new List<MergeInfo>();

            logger.LogInformation(
                "Found {GroupCount} groups of existing chapter parts for merging and {AdditionCount} groups for addition to existing merged chapters",
                chaptersToMerge.Count,
                chaptersToAddToExisting.Count
            );

            // Process each group for merging
            foreach (var (baseNumber, chapterParts) in chaptersToMerge)
            {
                try
                {
                    logger.LogInformation(
                        "Merging {PartCount} existing chapter parts for base number {BaseNumber}",
                        chapterParts.Count,
                        baseNumber
                    );

                    // Create target metadata for merged chapter
                    var targetMetadata = new ExtractedMetadata(
                        seriesTitle,
                        GenerateMergedChapterTitle(
                            chapterParts.First().Metadata?.ChapterTitle
                                ?? Path.GetFileNameWithoutExtension(
                                    chapterParts.First().RelativePath
                                ),
                            baseNumber
                        ),
                        baseNumber
                    );

                    // Check if the merged file would already exist before attempting merge
                    string potentialMergedFileName = GenerateMergedFileName(
                        chapterParts.First(),
                        baseNumber
                    );
                    string potentialMergedFilePath = Path.Combine(
                        seriesDirectoryPath,
                        potentialMergedFileName
                    );

                    if (File.Exists(potentialMergedFilePath))
                    {
                        logger.LogWarning(
                            "Skipping merge for base number {BaseNumber} because merged file {MergedFileName} already exists at {MergedFilePath}. "
                                + "This likely means the merge was already completed in a previous operation.",
                            baseNumber,
                            potentialMergedFileName,
                            potentialMergedFilePath
                        );
                        continue; // Skip this merge and continue with the next one
                    }

                    // Merge the chapters - use seriesDirectoryPath for both reading and writing
                    var (mergedChapter, originalParts) = await MergeChapterPartsAsync(
                        chapterParts,
                        seriesDirectoryPath,
                        seriesDirectoryPath,
                        baseNumber,
                        targetMetadata,
                        null, // For existing chapter merging, paths should be correct already
                        cancellationToken
                    );

                    // Fix the RelativePath to be relative to library root instead of series directory
                    var correctedMergedChapter = new FoundChapter(
                        mergedChapter.FileName,
                        Path.Combine(
                            PathEscaper.EscapeFileName(seriesTitle),
                            mergedChapter.FileName
                        ),
                        mergedChapter.StorageType,
                        mergedChapter.Metadata
                    );

                    mergeInformation.Add(
                        new MergeInfo(correctedMergedChapter, originalParts, baseNumber)
                    );

                    // Delete original chapter part files after successful merging
                    foreach (FoundChapter part in chapterParts)
                    {
                        try
                        {
                            string partFilePath = Path.Combine(
                                seriesDirectoryPath,
                                part.RelativePath
                            );
                            if (File.Exists(partFilePath))
                            {
                                File.Delete(partFilePath);
                                logger.LogInformation(
                                    "Deleted original chapter part file during merge: {FilePath}",
                                    partFilePath
                                );
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Original chapter part file not found for deletion during merge: {FilePath}",
                                    partFilePath
                                );
                            }
                        }
                        catch (Exception deleteEx)
                        {
                            logger.LogError(
                                deleteEx,
                                "Failed to delete original chapter part file during merge: {PartFileName}",
                                part.FileName
                            );
                        }
                    }

                    logger.LogInformation(
                        "Successfully merged {PartCount} existing chapter parts into {MergedFileName}",
                        chapterParts.Count,
                        mergedChapter.FileName
                    );
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to merge existing chapter parts for base number {BaseNumber}",
                        baseNumber
                    );
                }
            }

            // Process chapters that can be added to existing merged chapters
            foreach (var (baseNumber, chapterParts) in chaptersToAddToExisting)
            {
                try
                {
                    logger.LogInformation(
                        "Preparing {PartCount} chapter parts for addition to existing merged chapter {BaseNumber}",
                        chapterParts.Count,
                        baseNumber
                    );

                    // Create target metadata for the chapters to be added
                    var targetMetadata = new ExtractedMetadata(
                        seriesTitle,
                        GenerateMergedChapterTitle(
                            chapterParts.First().Metadata?.ChapterTitle
                                ?? Path.GetFileNameWithoutExtension(
                                    chapterParts.First().RelativePath
                                ),
                            baseNumber
                        ),
                        baseNumber
                    );

                    // Create OriginalChapterPart objects for the chapters that will be added
                    var originalParts = new List<OriginalChapterPart>();
                    foreach (FoundChapter chapterPart in chapterParts)
                    {
                        string chapterNumber = ExtractChapterNumber(chapterPart) ?? baseNumber;
                        originalParts.Add(
                            new OriginalChapterPart
                            {
                                FileName = chapterPart.FileName,
                                ChapterNumber = chapterNumber,
                                Metadata = chapterPart.Metadata,
                                PageNames = new List<string>(), // Will be populated when actually processing the file
                                StartPageIndex = 0, // Will be set by the coordinator when actually adding to the merged file
                                EndPageIndex = 0, // Will be set by the coordinator when actually adding to the merged file
                            }
                        );
                    }

                    // Create a dummy merged chapter (the actual merged chapter already exists)
                    // This is just for the MergeInfo structure - use the correct filename format
                    string mergedFileName = GenerateMergedFileName(
                        chapterParts.First(),
                        baseNumber
                    );
                    var dummyMergedChapter = new FoundChapter(
                        mergedFileName,
                        Path.Combine(PathEscaper.EscapeFileName(seriesTitle), mergedFileName),
                        ChapterStorageType.Cbz,
                        targetMetadata
                    );

                    mergeInformation.Add(
                        new MergeInfo(dummyMergedChapter, originalParts, baseNumber)
                    );

                    logger.LogInformation(
                        "Successfully prepared {PartCount} chapter parts for addition to existing merged chapter {BaseNumber}",
                        chapterParts.Count,
                        baseNumber
                    );
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to prepare chapter parts for addition to existing merged chapter {BaseNumber}",
                        baseNumber
                    );
                }
            }

            return new ChapterMergeResult(new List<FoundChapter>(), mergeInformation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during existing chapter merging");
            return new ChapterMergeResult(new List<FoundChapter>(), new List<MergeInfo>());
        }
    }

    public async Task<(
        FoundChapter mergedChapter,
        List<OriginalChapterPart> originalParts
    )> MergeChapterPartsAsync(
        List<FoundChapter> chapterParts,
        string basePath,
        string outputPath,
        string baseChapterNumber,
        ExtractedMetadata targetMetadata,
        Func<FoundChapter, string>? getActualFilePath = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!chapterParts.Any())
        {
            throw new ArgumentException("No chapter parts provided", nameof(chapterParts));
        }

        // Sort chapter parts by their part number to ensure correct order
        List<FoundChapter> sortedParts = chapterParts
            .Select(c => new
            {
                Chapter = c,
                PartNumber = ExtractPartNumber(ExtractChapterNumber(c)),
            })
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
        string finalMergedFilePath = Path.Combine(outputPath, mergedFileName);

        logger.LogInformation(
            "Attempting to create merged file: {MergedFileName} at path: {MergedFilePath}",
            mergedFileName,
            finalMergedFilePath
        );

        if (File.Exists(finalMergedFilePath))
        {
            logger.LogWarning(
                "Merge-target file {MergedFile} already exists at {MergedFilePath}. "
                    + "This may be from a previous merge operation. Skipping merge for safety.",
                mergedFileName,
                finalMergedFilePath
            );

            // Instead of throwing an exception, we should return an indication that this merge was skipped
            // For now, we'll throw but with more context about where to find the file
            throw new InvalidOperationException(
                localizer[
                    "Error_MergeTargetFileExists",
                    mergedFileName,
                    finalMergedFilePath,
                    Path.GetDirectoryName(finalMergedFilePath)
                ]
            );
        }

        // Create temporary directory for merge operation
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"manga_merge_{Guid.NewGuid():N}");
        string tempMergedFilePath = Path.Combine(tempDirectory, mergedFileName);

        try
        {
            Directory.CreateDirectory(tempDirectory);
            logger.LogDebug("Created temporary merge directory: {TempDirectory}", tempDirectory);

            // Create the merged CBZ file in temporary location
            await using (
                ZipArchive mergedArchive = await ZipFile.OpenAsync(
                    tempMergedFilePath,
                    ZipArchiveMode.Create,
                    cancellationToken
                )
            )
            {
                int pageCounter = 0;

                foreach (FoundChapter part in sortedParts)
                {
                    // Use the custom path function if provided, otherwise use the default path construction
                    string partPath =
                        getActualFilePath?.Invoke(part)
                        ?? Path.Combine(basePath, part.RelativePath);
                    var pageNames = new List<string>();
                    int startPageIndex = pageCounter;

                    // Read pages from the part's CBZ file
                    await using (
                        ZipArchive partArchive = await ZipFile.OpenReadAsync(
                            partPath,
                            cancellationToken
                        )
                    )
                    {
                        // Get image entries, sorted by name for consistent ordering
                        List<ZipArchiveEntry> imageEntries = partArchive
                            .Entries.Where(e => IsImageFile(e.Name) && !e.FullName.EndsWith("/"))
                            .OrderBy(e => e.Name, new NaturalStringComparer())
                            .ToList();

                        foreach (ZipArchiveEntry entry in imageEntries)
                        {
                            // Generate new page name with proper padding
                            string newPageName = $"{pageCounter:D4}{Path.GetExtension(entry.Name)}";
                            pageNames.Add(entry.Name); // Store original name

                            // Copy the image to the merged archive
                            ZipArchiveEntry newEntry = mergedArchive.CreateEntry(newPageName);
                            await using (
                                Stream originalStream = await entry.OpenAsync(cancellationToken)
                            )
                            await using (
                                Stream newStream = await newEntry.OpenAsync(cancellationToken)
                            )
                            {
                                await originalStream.CopyToAsync(newStream, cancellationToken);
                            }

                            pageCounter++;
                        }

                        // Copy ComicInfo.xml if it exists (we'll update it later)
                        ZipArchiveEntry? comicInfoEntry = partArchive.GetEntry("ComicInfo.xml");
                        if (comicInfoEntry != null && originalParts.Count == 0) // Only copy from first part
                        {
                            ZipArchiveEntry newComicInfo = mergedArchive.CreateEntry(
                                "ComicInfo.xml"
                            );
                            await using (
                                Stream originalStream = await comicInfoEntry.OpenAsync(
                                    cancellationToken
                                )
                            )
                            await using (
                                Stream newStream = await newComicInfo.OpenAsync(cancellationToken)
                            )
                            {
                                await originalStream.CopyToAsync(newStream, cancellationToken);
                            }
                        }
                    }

                    // Store original part information including raw ComicInfo.xml
                    string chapterNumber = ExtractChapterNumber(part) ?? baseChapterNumber;
                    string? originalComicInfoXml = null;

                    // Extract raw ComicInfo.xml for complete preservation
                    try
                    {
                        await using (
                            ZipArchive partArchive = await ZipFile.OpenReadAsync(
                                partPath,
                                cancellationToken
                            )
                        )
                        {
                            ZipArchiveEntry? comicInfoEntry = partArchive.GetEntry("ComicInfo.xml");
                            if (comicInfoEntry != null)
                            {
                                await using (
                                    Stream stream = await comicInfoEntry.OpenAsync(
                                        cancellationToken
                                    )
                                )
                                using (var reader = new StreamReader(stream))
                                {
                                    originalComicInfoXml = await reader.ReadToEndAsync(
                                        cancellationToken
                                    );
                                }

                                logger.LogDebug(
                                    "Captured original ComicInfo.xml for part {FileName} ({Length} characters)",
                                    part.FileName,
                                    originalComicInfoXml.Length
                                );
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to extract ComicInfo.xml from {FileName} - will use basic metadata only",
                            part.FileName
                        );
                    }

                    originalParts.Add(
                        new OriginalChapterPart
                        {
                            FileName = part.FileName,
                            ChapterNumber = chapterNumber,
                            Metadata = part.Metadata,
                            PageNames = pageNames,
                            StartPageIndex = startPageIndex,
                            EndPageIndex = pageCounter - 1,
                            OriginalComicInfoXml = originalComicInfoXml,
                        }
                    );
                }
            }

            // Update the merged CBZ's ComicInfo.xml with the target metadata
            await metadataHandling.WriteComicInfoAsync(tempMergedFilePath, targetMetadata);

            // Ensure the output directory exists
            Directory.CreateDirectory(outputPath);

            // Move the successfully created file from temp to final location
            File.Move(tempMergedFilePath, finalMergedFilePath);
            logger.LogDebug(
                "Moved merged file from temporary location to final path: {FinalPath}",
                finalMergedFilePath
            );

            var mergedChapter = new FoundChapter(
                mergedFileName,
                Path.GetRelativePath(outputPath, finalMergedFilePath),
                ChapterStorageType.Cbz,
                targetMetadata
            );

            logger.LogInformation(
                "Merged {PartCount} chapter parts into {MergedFile}",
                sortedParts.Count,
                mergedFileName
            );

            return (mergedChapter, originalParts);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to merge chapter parts into {MergedFileName}. Cleaning up temporary files.",
                mergedFileName
            );
            throw;
        }
        finally
        {
            // Clean up temporary directory and any files that may have been created
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                    logger.LogDebug(
                        "Cleaned up temporary merge directory: {TempDirectory}",
                        tempDirectory
                    );
                }
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(
                    cleanupEx,
                    "Failed to clean up temporary merge directory: {TempDirectory}",
                    tempDirectory
                );
            }
        }
    }

    public async Task<List<FoundChapter>> RestoreChapterPartsAsync(
        string mergedChapterPath,
        List<OriginalChapterPart> originalParts,
        string outputDirectory,
        CancellationToken cancellationToken = default
    )
    {
        var restoredChapters = new List<FoundChapter>();

        await using (
            ZipArchive mergedArchive = await ZipFile.OpenReadAsync(
                mergedChapterPath,
                cancellationToken
            )
        )
        {
            foreach (OriginalChapterPart originalPart in originalParts)
            {
                string partPath = Path.Combine(outputDirectory, originalPart.FileName);

                // Create the individual part CBZ
                await using (
                    ZipArchive partArchive = await ZipFile.OpenAsync(
                        partPath,
                        ZipArchiveMode.Create,
                        cancellationToken
                    )
                )
                {
                    // Extract pages for this part
                    for (int i = originalPart.StartPageIndex; i <= originalPart.EndPageIndex; i++)
                    {
                        ZipArchiveEntry? mergedEntry = FindPageByIndex(mergedArchive, i);

                        if (mergedEntry != null)
                        {
                            // Restore original page name if available (backward compatibility for legacy records)
                            string originalPageName;
                            int pageIndexWithinPart = i - originalPart.StartPageIndex;

                            if (
                                originalPart.PageNames != null
                                && pageIndexWithinPart >= 0
                                && pageIndexWithinPart < originalPart.PageNames.Count
                            )
                            {
                                // Use original page name from stored metadata
                                originalPageName = originalPart.PageNames[pageIndexWithinPart];
                                logger.LogDebug(
                                    "Using stored original page name: {PageName} for page index {PageIndex}",
                                    originalPageName,
                                    i
                                );
                            }
                            else
                            {
                                // Fallback for legacy records without PageNames or incomplete data
                                originalPageName =
                                    $"{pageIndexWithinPart:D4}{Path.GetExtension(mergedEntry.Name)}";
                                logger.LogDebug(
                                    "Using generated page name: {PageName} for page index {PageIndex} (legacy compatibility)",
                                    originalPageName,
                                    i
                                );
                            }

                            ZipArchiveEntry partEntry = partArchive.CreateEntry(originalPageName);
                            await using (
                                Stream mergedStream = await mergedEntry.OpenAsync(cancellationToken)
                            )
                            await using (
                                Stream partStream = await partEntry.OpenAsync(cancellationToken)
                            )
                            {
                                await mergedStream.CopyToAsync(partStream, cancellationToken);
                            }
                        }
                        else
                        {
                            logger.LogWarning(
                                "Could not find page at index {PageIndex} in merged archive {MergedFile}",
                                i,
                                Path.GetFileName(mergedChapterPath)
                            );
                        }
                    }

                    // Create ComicInfo.xml with original content if available, otherwise use metadata
                    if (!string.IsNullOrEmpty(originalPart.OriginalComicInfoXml))
                    {
                        // Use the complete original ComicInfo.xml content for full metadata preservation
                        ZipArchiveEntry comicInfoEntry = partArchive.CreateEntry("ComicInfo.xml");
                        await using (
                            Stream comicInfoStream = await comicInfoEntry.OpenAsync(
                                cancellationToken
                            )
                        )
                        await using (var writer = new StreamWriter(comicInfoStream))
                        {
                            await writer.WriteAsync(originalPart.OriginalComicInfoXml);
                        }

                        logger.LogDebug(
                            "Restored original ComicInfo.xml for {FileName} ({Length} characters)",
                            originalPart.FileName,
                            originalPart.OriginalComicInfoXml.Length
                        );
                    }
                    else
                    {
                        // Fallback to generating ComicInfo.xml from basic metadata (backward compatibility)
                        await metadataHandling.WriteComicInfoAsync(
                            partArchive,
                            originalPart.Metadata
                        );
                        logger.LogDebug(
                            "Generated ComicInfo.xml from basic metadata for {FileName} (legacy compatibility)",
                            originalPart.FileName
                        );
                    }
                }

                restoredChapters.Add(
                    new FoundChapter(
                        originalPart.FileName,
                        Path.GetRelativePath(outputDirectory, partPath),
                        ChapterStorageType.Cbz,
                        originalPart.Metadata
                    )
                );
            }
        }

        logger.LogInformation(
            "Restored {PartCount} chapter parts from {MergedFile}",
            originalParts.Count,
            Path.GetFileName(mergedChapterPath)
        );

        return restoredChapters;
    }

    /// <summary>
    /// Finds a page entry in the merged archive by index, handling different naming conventions including non-numeric names
    /// </summary>
    private ZipArchiveEntry? FindPageByIndex(ZipArchive mergedArchive, int pageIndex)
    {
        // First, get all image entries sorted naturally to establish the canonical order
        var imageEntries = mergedArchive
            .Entries.Where(e => IsImageFile(e.Name) && !e.FullName.EndsWith("/"))
            .OrderBy(e => e.Name, new NaturalStringComparer())
            .ToList();

        // If pageIndex is within range, return by position (most reliable for any naming scheme)
        if (pageIndex >= 0 && pageIndex < imageEntries.Count)
        {
            return imageEntries[pageIndex];
        }

        // If out of range, try numeric matching as fallback for edge cases
        // This handles specific numeric patterns that might exist
        var possibleFormats = new[]
        {
            $"{pageIndex:D4}", // 4-digit padding (our standard)
            $"{pageIndex:D3}", // 3-digit padding
            $"{pageIndex:D2}", // 2-digit padding
            $"{pageIndex:D1}", // 1-digit (no padding)
            pageIndex.ToString(), // Plain number
        };

        foreach (string format in possibleFormats)
        {
            ZipArchiveEntry? entry = imageEntries.FirstOrDefault(e =>
                e.Name.StartsWith(format + ".")
                || e.Name.StartsWith(format + "_")
                || e.Name.Equals(
                    format + Path.GetExtension(e.Name),
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (entry != null)
            {
                logger.LogDebug(
                    "Found page {PageIndex} using numeric format '{Format}': {EntryName}",
                    pageIndex,
                    format,
                    entry.Name
                );
                return entry;
            }
        }

        logger.LogWarning(
            "Could not find page at index {PageIndex} in archive with {TotalPages} pages. Available pages: {PageNames}",
            pageIndex,
            imageEntries.Count,
            string.Join(", ", imageEntries.Take(5).Select(e => e.Name))
        );

        return null;
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
        return ChapterNumberHelper.ExtractChapterNumber(fileName);
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
            Match match = ChapterNumberHelper
                .ChapterNumberRegex()
                .Match(chapter.Metadata.ChapterTitle);
            if (match.Success)
            {
                string num = match.Groups["num"].Value;
                string subnum = match.Groups["subnum"].Value;
                return string.IsNullOrEmpty(subnum) ? num : $"{num}.{subnum}";
            }
        }

        // Try to extract from filename
        Match fileMatch = ChapterNumberHelper.ChapterNumberRegex().Match(chapter.FileName);
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
        int dotIndex = chapterNumber.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex > 0)
        {
            return chapterNumber[..dotIndex];
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
        string baseFileName = ChapterNumberHelper
            .ChapterNumberRegex()
            .Replace(
                nameWithoutExtension,
                match =>
                    match.Value.Replace(
                        match.Groups["num"].Value
                            + (
                                string.IsNullOrEmpty(match.Groups["subnum"].Value)
                                    ? ""
                                    : "." + match.Groups["subnum"].Value
                            ),
                        baseChapterNumber
                    )
            );

        return $"{baseFileName}{extension}";
    }

    private string? GenerateMergedChapterTitle(string? originalTitle, string baseChapterNumber)
    {
        if (string.IsNullOrEmpty(originalTitle))
        {
            return null;
        }

        // Replace the chapter number in the title with the base number
        return ChapterNumberHelper
            .ChapterNumberRegex()
            .Replace(
                originalTitle,
                match =>
                    match.Value.Replace(
                        match.Groups["num"].Value
                            + (
                                string.IsNullOrEmpty(match.Groups["subnum"].Value)
                                    ? ""
                                    : "." + match.Groups["subnum"].Value
                            ),
                        baseChapterNumber
                    )
            );
    }

    /// <summary>
    ///     Extracts the longest consecutive sequence of chapter parts from the beginning.
    ///     This allows merging even when there are gaps (e.g., 1.1, 1.2, 1.5 will merge 1.1 and 1.2).
    ///     Supports flexible sequences:
    ///     - Starting with base number (e.g., 22, 22.1, 22.2)
    ///     - Starting with .1 (e.g., 22.1, 22.2, 22.3)
    ///     - Special case: base followed directly by .2 (e.g., 22, 22.2, 22.3)
    /// </summary>
    /// <param name="chapters">All chapters for a given base number</param>
    /// <param name="baseNumber">The base chapter number</param>
    /// <returns>A list containing only the consecutive chapters from the start</returns>
    private List<FoundChapter> GetConsecutiveChapterParts(
        List<FoundChapter> chapters,
        string baseNumber
    )
    {
        if (chapters.Count < 2)
        {
            return new List<FoundChapter>();
        }

        // Extract and parse chapter numbers in a single pass for efficiency
        var chaptersWithNumbers = chapters
            .Select(chapter =>
            {
                string? chapterNum = ExtractChapterNumber(chapter);
                return new
                {
                    Chapter = chapter,
                    Number = chapterNum != null && decimal.TryParse(chapterNum, out decimal num)
                        ? (decimal?)num
                        : null,
                };
            })
            .Where(x => x.Number.HasValue)
            .OrderBy(x => x.Number!.Value)
            .ToList();

        if (chaptersWithNumbers.Count < 2)
        {
            return new List<FoundChapter>();
        }

        // Check for duplicates
        if (
            chaptersWithNumbers.Select(x => x.Number!.Value).Distinct().Count()
            != chaptersWithNumbers.Count
        )
        {
            return new List<FoundChapter>();
        }

        if (!decimal.TryParse(baseNumber, out decimal baseNum))
        {
            return new List<FoundChapter>();
        }

        var consecutiveChapters = new List<FoundChapter>();

        // Process each chapter to find the consecutive sequence
        for (int i = 0; i < chaptersWithNumbers.Count; i++)
        {
            decimal currentNumber = chaptersWithNumbers[i].Number!.Value;

            if (i == 0)
            {
                // First chapter: validate starting position
                if (Math.Abs(currentNumber - baseNum) < DecimalComparisonTolerance)
                {
                    // Starting with base number is valid
                    consecutiveChapters.Add(chaptersWithNumbers[i].Chapter);
                }
                else if (
                    Math.Abs(currentNumber - (baseNum + ChapterPartIncrement))
                    < DecimalComparisonTolerance
                )
                {
                    // Starting with .1 is valid
                    consecutiveChapters.Add(chaptersWithNumbers[i].Chapter);
                }
                else
                {
                    // Invalid start (e.g., starting with .3)
                    break;
                }
            }
            else
            {
                // Not the first chapter: check if it's consecutive
                decimal previousNumber = chaptersWithNumbers[i - 1].Number!.Value;

                if (Math.Abs(previousNumber - baseNum) < DecimalComparisonTolerance)
                {
                    // After base, can be .1 or .2
                    if (
                        Math.Abs(currentNumber - (baseNum + ChapterPartIncrement))
                            < DecimalComparisonTolerance
                        || Math.Abs(currentNumber - (baseNum + (ChapterPartIncrement * 2)))
                            < DecimalComparisonTolerance
                    )
                    {
                        consecutiveChapters.Add(chaptersWithNumbers[i].Chapter);
                    }
                    else
                    {
                        // Gap detected after base number
                        break;
                    }
                }
                else
                {
                    // After a decimal part, must increment by 0.1
                    decimal expectedNext = previousNumber + ChapterPartIncrement;
                    if (Math.Abs(currentNumber - expectedNext) < DecimalComparisonTolerance)
                    {
                        consecutiveChapters.Add(chaptersWithNumbers[i].Chapter);
                    }
                    else
                    {
                        // Gap detected
                        break;
                    }
                }
            }
        }

        return consecutiveChapters;
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
    ///     - 22.1, 22.1 ✗ (duplicate chapter numbers)
    /// </summary>
    private bool AreConsecutiveChapterParts(List<FoundChapter> chapters, string baseNumber)
    {
        if (chapters.Count < 2)
        {
            return false;
        }

        // Extract and sort the decimal parts
        List<decimal> chapterNumbers = chapters
            .Select(ExtractChapterNumber)
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

        // Check for duplicate chapter numbers - don't allow merging of chapters with the same number
        if (chapterNumbers.Distinct().Count() != chapterNumbers.Count)
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
                    if (
                        nextNumber != baseNum + ChapterPartIncrement
                        && nextNumber != baseNum + (ChapterPartIncrement * 2)
                    )
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
                        if (
                            decimalPart != ChapterPartIncrement
                            && decimalPart != ChapterPartIncrement * 2
                        )
                        {
                            return false; // Invalid decimal part after base
                        }
                    }
                    else
                    {
                        // Subsequent decimals must increment by 0.1
                        expectedValue = chapterNumbers[i - 1] + ChapterPartIncrement;
                    }
                }
                else
                {
                    // Sequence starts with decimal (e.g., 22.1, 22.2, 22.3)
                    if (i == 0)
                    {
                        // First number should be base + 0.1
                        expectedValue = baseNum + ChapterPartIncrement;
                    }
                    else
                    {
                        // Subsequent numbers increment by 0.1
                        expectedValue = chapterNumbers[i - 1] + ChapterPartIncrement;
                    }
                }

                // Check if current number matches expected (with floating point tolerance)
                if (Math.Abs(currentNumber - expectedValue) > DecimalComparisonTolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Determines if a chapter number represents a chapter part that can be added to a merged chapter.
    /// For example, "2.3" is a part of base chapter "2".
    /// </summary>
    /// <param name="chapterNumber">The full chapter number (e.g., "2.3")</param>
    /// <param name="baseNumber">The base chapter number (e.g., "2")</param>
    /// <returns>True if this is a valid chapter part for the base number</returns>
    private bool IsChapterPart(string chapterNumber, string baseNumber)
    {
        if (!decimal.TryParse(chapterNumber, out decimal fullNumber))
        {
            return false;
        }

        if (!decimal.TryParse(baseNumber, out decimal baseNum))
        {
            return false;
        }

        // Check if the chapter number starts with the base number and has a decimal part
        decimal decimalPart = fullNumber - baseNum;

        // Must have a positive decimal part less than 1
        if (decimalPart <= 0 || decimalPart >= 1)
        {
            return false;
        }

        // Check the original string format to ensure it's a valid single decimal digit (e.g., .1, .2, not .10)
        int dotIndex = chapterNumber.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex > 0 && dotIndex < chapterNumber.Length - 1)
        {
            string decimalPartStr = chapterNumber[(dotIndex + 1)..];
            // Only allow single digit decimal parts (1-9)
            return decimalPartStr.Length == 1
                && char.IsDigit(decimalPartStr[0])
                && decimalPartStr != "0";
        }

        return false;
    }

    /// <summary>
    /// Creates a temporary FoundChapter for internal processing.
    /// </summary>
    private static FoundChapter CreateFoundChapter(string fileName, string chapterNumber)
    {
        return new FoundChapter(
            fileName,
            fileName,
            ChapterStorageType.Cbz,
            new ExtractedMetadata("", null, chapterNumber)
        );
    }

    private static bool IsImageFile(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return ImageConstants.IsSupportedImageExtension(extension);
    }
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
