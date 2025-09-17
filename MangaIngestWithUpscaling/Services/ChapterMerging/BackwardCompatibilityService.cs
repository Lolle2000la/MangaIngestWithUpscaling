using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

/// <summary>
/// Service to ensure backward compatibility with existing merged chapter records
/// from before the enhanced merging functionality was added
/// </summary>
[RegisterScoped]
public class BackwardCompatibilityService(
    ApplicationDbContext dbContext,
    ILogger<BackwardCompatibilityService> logger) : IBackwardCompatibilityService
{
    /// <summary>
    /// Validates and ensures existing merged chapter records are compatible with enhanced functionality
    /// </summary>
    public async Task ValidateAndUpgradeExistingRecordsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting backward compatibility validation for existing merged chapter records");

        // Get all existing merged chapter records
        List<MergedChapterInfo> existingRecords = await dbContext.MergedChapterInfos
            .Include(m => m.Chapter)
            .ThenInclude(c => c.Manga)
            .ThenInclude(m => m.Library)
            .ToListAsync(cancellationToken);

        if (!existingRecords.Any())
        {
            logger.LogInformation("No existing merged chapter records found - no compatibility validation needed");
            return;
        }

        logger.LogInformation("Found {RecordCount} existing merged chapter records to validate", existingRecords.Count);

        int validatedCount = 0;
        int upgradeCount = 0;
        var issues = new List<string>();

        foreach (MergedChapterInfo record in existingRecords)
        {
            try
            {
                bool wasUpgraded = await ValidateAndUpgradeRecordAsync(record, cancellationToken);
                validatedCount++;
                if (wasUpgraded)
                {
                    upgradeCount++;
                }
            }
            catch (Exception ex)
            {
                string issue = $"Failed to validate record for chapter {record.Chapter.FileName}: {ex.Message}";
                logger.LogWarning(ex, "Backward compatibility validation failed for chapter {ChapterFile}", record.Chapter.FileName);
                issues.Add(issue);
            }
        }

        // Save any upgrades made
        if (upgradeCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Upgraded {UpgradeCount} merged chapter records for enhanced compatibility", upgradeCount);
        }

        logger.LogInformation("Backward compatibility validation complete: {ValidatedCount} validated, {UpgradeCount} upgraded, {IssueCount} issues",
            validatedCount, upgradeCount, issues.Count);

        if (issues.Any())
        {
            logger.LogWarning("Compatibility validation issues detected: {Issues}", string.Join("; ", issues));
        }
    }

    /// <summary>
    /// Validates a single merged chapter record and upgrades it if necessary
    /// </summary>
    private async Task<bool> ValidateAndUpgradeRecordAsync(MergedChapterInfo record, CancellationToken cancellationToken)
    {
        bool wasUpgraded = false;

        // Validate that OriginalParts is properly populated
        if (record.OriginalParts == null || !record.OriginalParts.Any())
        {
            logger.LogWarning("Merged chapter {ChapterFile} has no original parts - this may cause issues with reversion",
                record.Chapter.FileName);
            return false;
        }

        // Validate page ranges in original parts
        wasUpgraded |= await ValidateAndFixPageRangesAsync(record);

        // Validate that the merged chapter file still exists
        wasUpgraded |= await ValidateChapterFileExistsAsync(record);

        // Ensure metadata is properly formatted for enhanced functionality
        wasUpgraded |= await ValidateAndUpgradeMetadataAsync(record);

        return wasUpgraded;
    }

    /// <summary>
    /// Validates and fixes page ranges in original parts if they are inconsistent
    /// </summary>
    private Task<bool> ValidateAndFixPageRangesAsync(MergedChapterInfo record)
    {
        bool wasFixed = false;

        // Check for overlapping or inconsistent page ranges
        var sortedParts = record.OriginalParts.OrderBy(p => p.StartPageIndex).ToList();
        
        for (int i = 0; i < sortedParts.Count; i++)
        {
            var part = sortedParts[i];

            // Validate that EndPageIndex >= StartPageIndex
            if (part.EndPageIndex < part.StartPageIndex)
            {
                logger.LogWarning("Fixing invalid page range for part {PartFileName}: {StartPage}-{EndPage}",
                    part.FileName, part.StartPageIndex, part.EndPageIndex);
                part.EndPageIndex = part.StartPageIndex + Math.Max(0, part.PageNames.Count - 1);
                wasFixed = true;
            }

            // Validate that page ranges don't overlap (except at boundaries)
            if (i > 0)
            {
                var previousPart = sortedParts[i - 1];
                if (part.StartPageIndex <= previousPart.EndPageIndex)
                {
                    logger.LogWarning("Fixing overlapping page ranges between {PrevPart} and {CurrentPart}",
                        previousPart.FileName, part.FileName);
                    part.StartPageIndex = previousPart.EndPageIndex + 1;
                    part.EndPageIndex = part.StartPageIndex + Math.Max(0, part.PageNames.Count - 1);
                    wasFixed = true;
                }
            }
        }

        return Task.FromResult(wasFixed);
    }

    /// <summary>
    /// Validates that the merged chapter file still exists
    /// </summary>
    private Task<bool> ValidateChapterFileExistsAsync(MergedChapterInfo record)
    {
        string chapterPath = Path.Combine(record.Chapter.Manga.Library.NotUpscaledLibraryPath, record.Chapter.RelativePath);
        
        if (!File.Exists(chapterPath))
        {
            logger.LogWarning("Merged chapter file no longer exists: {ChapterPath} - reversion may not work properly", chapterPath);
            // We don't "fix" this automatically since it requires user intervention
            // Just log the issue for user awareness
        }

        return Task.FromResult(false); // No automatic fix applied
    }

    /// <summary>
    /// Validates and upgrades metadata for enhanced functionality compatibility
    /// </summary>
    private Task<bool> ValidateAndUpgradeMetadataAsync(MergedChapterInfo record)
    {
        bool upgraded = false;

        foreach (var part in record.OriginalParts)
        {
            // Ensure PageNames is populated - critical for enhanced restoration
            if (part.PageNames == null || !part.PageNames.Any())
            {
                logger.LogWarning("Original part {PartFileName} has no page names - generating default names", part.FileName);
                
                // Generate default page names based on page range
                int pageCount = part.EndPageIndex - part.StartPageIndex + 1;
                part.PageNames = Enumerable.Range(0, pageCount)
                    .Select(i => $"{i:D4}.jpg") // Use 4-digit format compatible with enhanced restoration
                    .ToList();
                
                upgraded = true;
            }

            // Ensure ChapterNumber is populated - needed for enhanced merge detection
            if (string.IsNullOrEmpty(part.ChapterNumber))
            {
                logger.LogWarning("Original part {PartFileName} has no chapter number - this may affect merge detection", part.FileName);
                // We can't automatically fix this without risking incorrect data
                // Log for user awareness
            }
        }

        return Task.FromResult(upgraded);
    }
}

/// <summary>
/// Interface for backward compatibility service
/// </summary>
public interface IBackwardCompatibilityService
{
    /// <summary>
    /// Validates and ensures existing merged chapter records are compatible with enhanced functionality
    /// </summary>
    Task ValidateAndUpgradeExistingRecordsAsync(CancellationToken cancellationToken = default);
}