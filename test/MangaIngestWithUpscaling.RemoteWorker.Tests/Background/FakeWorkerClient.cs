using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// Fake local worker: "upscales" each page by reversing its bytes and reports it through
/// <c>onPageDone</c>. Optionally throws after N pages to simulate a worker/connection drop.
/// </summary>
internal sealed class FakeWorkerClient : IMangaJaNaiWorkerClient
{
    public int? DropAfterPages { get; init; }

    /// <summary>Exception thrown at the drop point; defaults to a simulated I/O drop.</summary>
    public Exception? DropException { get; init; }
    public UpscaleJobStatus PageStatus { get; init; } = UpscaleJobStatus.Success;

    /// <summary>True once the page enumeration was cancelled (the chapter was stopped mid-stream).</summary>
    public bool Canceled { get; private set; }
    public int ProcessedPages { get; private set; }

    /// <summary>The pages the chapter consumed, in order, so a test can pin the yielded name and path.</summary>
    public List<ChapterPage> SeenPages { get; } = new();

    /// <summary>The bytes read for each consumed page (by index), captured at read time — before the
    /// work directory is torn down — so a test can pin the file the path actually pointed at.</summary>
    public Dictionary<int, byte[]> SeenPageBytes { get; } = new();

    /// <summary>The inactivity timeout the chapter was driven with, for tests that pin the scaling.</summary>
    public TimeSpan? LastTimeout { get; private set; }

    /// <summary>
    /// Invoked with each consumed page right after it is reported done, so a test can observe what
    /// the page-done callback left on disk (for example whether the fetched source copy was deleted).
    /// </summary>
    public Action<ChapterPage>? AfterPageDone { get; set; }

    /// <summary>Number of times the chapter boundary released the cached GPU session.</summary>
    public int ReleaseGpuCacheCalls { get; private set; }

    public async Task<UpscaleJobResult> RunChapterAsync(
        ChapterJobRequest request,
        IAsyncEnumerable<ChapterPage> pages,
        IProgress<UpscaleProgress>? progress,
        Action<UpscaleJobFile> onPageDone,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    )
    {
        LastTimeout = timeout;
        var files = new List<UpscaleJobFile>();
        try
        {
            await foreach (ChapterPage page in pages.WithCancellation(cancellationToken))
            {
                byte[] source = await File.ReadAllBytesAsync(page.Path, cancellationToken);
                SeenPages.Add(page);
                SeenPageBytes[page.Index] = source;
                string outputPath = Path.Combine(
                    request.OutputFolder,
                    $"{Path.GetFileNameWithoutExtension(page.Name)}.webp"
                );
                await File.WriteAllBytesAsync(
                    outputPath,
                    source.Reverse().ToArray(),
                    cancellationToken
                );

                var file = new UpscaleJobFile(page.Name, outputPath, PageStatus);
                files.Add(file);
                onPageDone(file);
                ProcessedPages++;
                AfterPageDone?.Invoke(page);

                if (DropAfterPages is int dropAt && ProcessedPages >= dropAt)
                {
                    throw DropException ?? new IOException("simulated drop");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Canceled = true;
            throw;
        }

        return new UpscaleJobResult(request.Id, "ok", files, 0);
    }

    public Task<UpscaleJobResult> RunJobAsync(
        UpscaleJobRequest request,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    ) => throw new NotSupportedException();

    public Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken)
    {
        ReleaseGpuCacheCalls++;
        return Task.FromResult(true);
    }
}
