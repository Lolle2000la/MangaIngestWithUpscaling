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
    public string PageStatus { get; init; } = "upscaled";
    public int ProcessedPages { get; private set; }

    public async Task<UpscaleJobResult> RunChapterAsync(
        ChapterJobRequest request,
        IAsyncEnumerable<ChapterPage> pages,
        IProgress<UpscaleProgress>? progress,
        Action<UpscaleJobFile> onPageDone,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    )
    {
        var files = new List<UpscaleJobFile>();
        await foreach (ChapterPage page in pages.WithCancellation(cancellationToken))
        {
            byte[] source = await File.ReadAllBytesAsync(page.Path, cancellationToken);
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

            if (DropAfterPages is int dropAt && ProcessedPages >= dropAt)
            {
                throw DropException ?? new IOException("simulated drop");
            }
        }

        return new UpscaleJobResult(request.Id, "ok", files, 0);
    }

    public Task<UpscaleJobResult> RunJobAsync(
        UpscaleJobRequest request,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    ) => throw new NotSupportedException();

    public Task ShutdownWorkerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
