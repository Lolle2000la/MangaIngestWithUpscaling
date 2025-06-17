using DynamicData.Kernel;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using System.IO.Compression;
using System.Xml.Linq;
using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition;

/// <summary>
/// Provides services for recognizing chapters in the ingest path.
/// Not everything in the ingest path is a chapter, so this service
/// identifies what is and what isn't.
/// </summary>
[RegisterScoped]
public class ChapterInIngestRecognitionService(
    IMetadataHandlingService metadataExtractionService,
    ILibraryFilteringService filteringService,
    ILogger<ChapterInIngestRecognitionService> logger) : IChapterInIngestRecognitionService
{
    /// <summary>
    /// Finds all chapters in the ingest path, processing files concurrently on background threads.
    /// </summary>
    /// <param name="ingestPath">The path to search for chapters.</param>
    /// <param name="libraryFilterRules">Optional rules to filter the found chapters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of found chapters.</returns>
    public async IAsyncEnumerable<FoundChapter> FindAllChaptersAt(string ingestPath, // <-- Added 'async'
        IReadOnlyList<LibraryFilterRule>? libraryFilterRules = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) // <-- Added [EnumeratorCancellation]
    {
        var channel = Channel.CreateUnbounded<FoundChapter>();

        // This producer task runs in the background. Its lifecycle is tied to the
        // consumption of the IAsyncEnumerable.
        _ = Task.Run(async () =>
        {
            try
            {
                var files = Directory.EnumerateFiles(ingestPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".cbz") || f.EndsWith("ComicInfo.xml"));

                var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

                await Parallel.ForEachAsync(files, parallelOptions, async (file, ct) =>
                {
                    FoundChapter? foundChapter = null;
                    try
                    {
                        var relativePath = Path.GetRelativePath(ingestPath, file);
                        var storageType = file.EndsWith(".cbz") ? ChapterStorageType.Cbz : ChapterStorageType.Folder;

                        var metadata = metadataExtractionService.GetSeriesAndTitleFromComicInfo(file);
                        foundChapter = new FoundChapter(Path.GetFileName(file), relativePath, storageType, metadata);

                        if (libraryFilterRules is { Count: > 0 } && filteringService.FilterChapter(foundChapter, libraryFilterRules))
                        {
                            foundChapter = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Don't let one bad file stop the whole process.
                        logger.LogError(ex, "Failed to process file {File}", file);
                    }

                    if (foundChapter != null)
                    {
                        await channel.Writer.WriteAsync(foundChapter, ct);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // This is an expected and clean way to exit when cancellation is requested.
            }
            catch (Exception ex)
            {
                // Log any unexpected error in the producer task itself.
                logger.LogError(ex, "An error occurred during the chapter discovery background task.");
                // Pass the exception to the channel so the consumer is aware of the failure.
                channel.Writer.Complete(ex);
            }
            finally
            {
                // CRUCIAL: Always complete the writer. This signals the consumer that no more items are coming.
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        // *** THE KEY CHANGE IS HERE ***
        // Instead of returning the channel reader directly, we consume it within the async iterator method.
        // This keeps the state machine alive and correctly handles completion.
        await foreach (var chapter in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chapter;
        }
    }
}