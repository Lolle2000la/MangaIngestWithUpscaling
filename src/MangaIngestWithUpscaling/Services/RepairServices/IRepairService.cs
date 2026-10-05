using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Services.RepairServices;

/// <summary>
/// Service for handling chapter repair operations, including preparation and merging of upscaled content.
/// </summary>
public interface IRepairService
{
    /// <summary>
    /// Prepares a repair context by analyzing differences, extracting pages, and creating temporary CBZ files.
    /// </summary>
    /// <param name="prepareMissingPagesCbz">
    /// When true (the default), also zips the extracted missing pages into a CBZ for batch upscaling.
    /// The remote page-streaming path reads the missing pages from the original source, so it passes
    /// false to skip that I/O.
    /// </param>
    RepairContext PrepareRepairContext(
        PageDifferenceResult differences,
        string originalPath,
        string upscaledPath,
        ILogger logger,
        bool prepareMissingPagesCbz = true
    );

    /// <summary>
    /// Merges upscaled missing pages back into the upscaled directory and creates the final CBZ.
    /// </summary>
    void MergeRepairResults(RepairContext context, string finalUpscaledPath, ILogger logger);

    /// <summary>
    /// Creates a CBZ file containing all missing pages for batch upscaling.
    /// </summary>
    Task<string> CreateMissingPagesBatch(
        string inputDir,
        ILogger logger,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Processes batch upscaled results and extracts them to the output directory.
    /// </summary>
    Task ProcessBatchUpscaleResults(
        string batchOutputCbz,
        string outputDir,
        int expectedPageCount,
        IProgress<UpscaleProgress> progress,
        ILogger logger,
        CancellationToken cancellationToken = default
    );
}
