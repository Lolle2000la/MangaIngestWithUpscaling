using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition;

/// <inheritdoc />
[RegisterScoped]
public class ChapterInIngestRecognitionService(
    IMetadataHandlingService metadataExtractionService,
    ILibraryFilteringService filteringService,
    ILogger<ChapterInIngestRecognitionService> logger
) : IChapterInIngestRecognitionService
{
    /// <inheritdoc />
    public async IAsyncEnumerable<FoundChapter> FindAllChaptersAt(
        string ingestPath,
        IReadOnlyList<LibraryFilterRule>? libraryFilterRules = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var channel = Channel.CreateUnbounded<FoundChapter>();

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var files = Directory
                        .EnumerateFiles(ingestPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".cbz") || f.EndsWith("ComicInfo.xml"));

                    var parallelOptions = new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                    };

                    await Parallel.ForEachAsync(
                        files,
                        parallelOptions,
                        async (file, ct) =>
                        {
                            FoundChapter? foundChapter = null;
                            try
                            {
                                var relativePath = Path.GetRelativePath(ingestPath, file);
                                var storageType = file.EndsWith(".cbz")
                                    ? ChapterStorageType.Cbz
                                    : ChapterStorageType.Folder;

                                var metadata =
                                    metadataExtractionService.GetSeriesAndTitleFromComicInfo(file);
                                foundChapter = new FoundChapter(
                                    Path.GetFileName(file),
                                    relativePath,
                                    storageType,
                                    metadata
                                );

                                if (
                                    libraryFilterRules is { Count: > 0 }
                                    && filteringService.FilterChapter(
                                        foundChapter,
                                        libraryFilterRules
                                    )
                                )
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
                        }
                    );
                }
                catch (OperationCanceledException)
                {
                    // The operation was cancelled, we can just exit gracefully.
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "An error occurred during the chapter discovery background task."
                    );
                    channel.Writer.Complete(ex);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            cancellationToken
        );

        await foreach (var chapter in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chapter;
        }
    }
}
