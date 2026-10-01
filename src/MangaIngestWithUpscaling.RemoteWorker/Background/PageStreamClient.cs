using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Runs one upscale task as a page stream: fetches the source pages the server is still missing,
/// feeds them to the local worker as they arrive, and uploads each upscaled page back as soon as it
/// is written. The server spools the pages and assembles the chapter, so a dropped connection
/// resumes at the first missing page.
/// </summary>
public sealed class PageStreamClient(
    IMangaJaNaiWorkerClient workerClient,
    IServiceScopeFactory scopeFactory,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<PageStreamClient> logger
)
{
    private const int ChunkSizeBytes = 1024 * 1024;
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Inactivity allowance for a streamed chapter. <see cref="UpscalerConfig.UpscaleTimeout"/> is a
    /// per-million-pixel allowance, so a chapter's slowest single page is a small multiple of it; the
    /// floor keeps slow hardware working while still letting the monitor kill a wedged worker.
    /// </summary>
    private TimeSpan ChapterInactivityTimeout
    {
        get
        {
            TimeSpan scaled = upscalerConfig.Value.UpscaleTimeout * 4;
            TimeSpan floor = TimeSpan.FromMinutes(15);
            return scaled > floor ? scaled : floor;
        }
    }

    public async Task RunAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        UpscalerProfile profile,
        CancellationToken stoppingToken
    )
    {
        PageManifestResponse manifest = await client.GetPageManifestAsync(
            new PageManifestRequest { TaskId = taskId },
            deadline: DateTime.UtcNow.Add(ManifestTimeout),
            cancellationToken: stoppingToken
        );

        if (manifest.Complete)
        {
            logger.LogInformation(
                "Task {TaskId} was already fully spooled; the server finalized it.",
                taskId
            );
            return;
        }

        if (manifest.Pages.Count == 0)
        {
            throw new InvalidOperationException($"Task {taskId} has no pages to upscale.");
        }

        Dictionary<int, string> nameByIndex = manifest.Pages.ToDictionary(
            p => p.Index,
            p => p.SourceName
        );
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var page in manifest.Pages)
        {
            indexByName.TryAdd(WorkerPageName(page.Index, page.SourceName), page.Index);
        }
        HashSet<int> completed = manifest.CompletedPages.ToHashSet();
        List<int> missing = manifest
            .Pages.Where(p => !completed.Contains(p.Index))
            .Select(p => p.Index)
            .ToList();

        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mangaingest_page_stream_{taskId}_{Guid.NewGuid():N}"
        );
        string sourceDirectory = Path.Combine(workDirectory, "source");
        string outputDirectory = Path.Combine(workDirectory, "output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            logger.LogInformation(
                "Streaming task {TaskId}: {Missing} of {Total} page(s) still need upscaling.",
                taskId,
                missing.Count,
                manifest.Pages.Count
            );

            var uploads = Channel.CreateUnbounded<PageUpload>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
            );
            var pageErrors = new System.Collections.Concurrent.ConcurrentBag<string>();

            Task uploadTask = UploadLoopAsync(
                client,
                taskId,
                manifest.TaskIdentity,
                uploads.Reader,
                stoppingToken
            );

            void OnPageDone(UpscaleJobFile file)
            {
                if (
                    file.Status == "upscaled"
                    && !string.IsNullOrEmpty(file.Output)
                    && indexByName.TryGetValue(file.Input, out int pageIndex)
                )
                {
                    uploads.Writer.TryWrite(new PageUpload(pageIndex, file.Output));
                }
                else
                {
                    // The whole-CBZ path fails the chapter when the engine reports an error
                    // ("DispatchDone"), so streaming does the same: record it and fail the task
                    // once the chapter unwinds, instead of silently producing a mixed chapter.
                    pageErrors.Add($"{file.Input}: {file.Status}");
                }
            }

            var chapterRequest = new ChapterJobRequest
            {
                Id = $"task-{taskId}-{Guid.NewGuid():N}",
                OutputFolder = outputDirectory,
                Format = profile.CompressionFormat,
                Scale = profile.ScalingFactor,
                TotalPages = manifest.Pages.Count,
            };

            await using var progressReporter = new StreamingProgressReporter(
                client,
                taskId,
                stoppingToken,
                logger
            );

            Task chapterTask = workerClient.RunChapterAsync(
                chapterRequest,
                FetchPagesAsync(
                    client,
                    taskId,
                    manifest.TaskIdentity,
                    missing,
                    nameByIndex,
                    sourceDirectory,
                    stoppingToken
                ),
                progressReporter.Progress,
                OnPageDone,
                stoppingToken,
                timeout: ChapterInactivityTimeout
            );

            try
            {
                await chapterTask;
            }
            finally
            {
                uploads.Writer.TryComplete();
                try
                {
                    await uploadTask;
                }
                catch (Exception) when (chapterTask.IsFaulted || chapterTask.IsCanceled)
                {
                    // The chapter already failed; surface that error rather than the upload loop's
                    // (usually consequential) failure.
                }
            }

            if (!pageErrors.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Page upscaling failed for task {taskId}: {string.Join("; ", pageErrors)}"
                );
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to delete page-stream work directory {Directory}.",
                    workDirectory
                );
            }
        }
    }

    /// <summary>
    /// Runs a split-detection task as a page stream: fetches the pages the server still needs,
    /// detects each one and uploads its result. The server finalizes the chapter's findings once
    /// every page has been reported.
    /// </summary>
    public async Task RunDetectionAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        CancellationToken stoppingToken
    )
    {
        PageManifestResponse manifest = await client.GetPageManifestAsync(
            new PageManifestRequest { TaskId = taskId },
            deadline: DateTime.UtcNow.Add(ManifestTimeout),
            cancellationToken: stoppingToken
        );

        if (manifest.Complete)
        {
            logger.LogInformation("Task {TaskId} was already fully detected.", taskId);
            return;
        }

        if (manifest.Pages.Count == 0)
        {
            throw new InvalidOperationException($"Task {taskId} has no pages to detect.");
        }

        Dictionary<int, string> nameByIndex = manifest.Pages.ToDictionary(
            p => p.Index,
            p => p.SourceName
        );
        HashSet<int> completed = manifest.CompletedPages.ToHashSet();
        List<int> missing = manifest
            .Pages.Where(p => !completed.Contains(p.Index))
            .Select(p => p.Index)
            .ToList();

        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mangaingest_page_detect_{taskId}_{Guid.NewGuid():N}"
        );
        string sourceDirectory = Path.Combine(workDirectory, "source");
        Directory.CreateDirectory(sourceDirectory);

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var detection = scope.ServiceProvider.GetRequiredService<ISplitDetectionService>();
            await using var progressReporter = new StreamingProgressReporter(
                client,
                taskId,
                stoppingToken,
                logger
            );

            int current = completed.Count;
            foreach (int pageIndex in missing)
            {
                stoppingToken.ThrowIfCancellationRequested();

                string sourceName = nameByIndex[pageIndex];
                string path = Path.Combine(
                    sourceDirectory,
                    $"{pageIndex:D5}_{Path.GetFileName(sourceName)}"
                );

                await FetchPageToFileAsync(
                    client,
                    taskId,
                    manifest.TaskIdentity,
                    pageIndex,
                    path,
                    stoppingToken
                );

                List<SplitDetectionResult> results = await detection.DetectSplitsAsync(
                    path,
                    progressReporter.Progress,
                    stoppingToken
                );

                // The detector echoes the temp file path it was given; report the chapter's own page
                // name instead, or the server keys the finding to the temp name and the split can
                // never be matched back to the page. Also merge any extra results for the page, since
                // the server stores exactly one result per page.
                SplitDetectionResult pageResult =
                    results.Count == 0
                        ? new SplitDetectionResult()
                        : results.Aggregate(SplitDetectionResultHelper.Merge);
                pageResult.ImagePath = sourceName;

                string json = JsonSerializer.Serialize(
                    pageResult,
                    SharedJsonContext.Default.SplitDetectionResult
                );
                UploadDetectionResultResponse response = await client.UploadPageDetectionAsync(
                    new UploadPageDetectionRequest
                    {
                        TaskId = taskId,
                        PageIndex = pageIndex,
                        ResultJson = json,
                        TaskIdentity = manifest.TaskIdentity,
                    },
                    deadline: DateTime.UtcNow.Add(PageTimeout),
                    cancellationToken: stoppingToken
                );
                if (!response.Success)
                {
                    throw new IOException(
                        $"Uploading the detection result for page {pageIndex} of task {taskId} failed: {response.Message}"
                    );
                }

                current++;
                progressReporter.Progress.Report(
                    new UpscaleProgress(manifest.Pages.Count, current, "Detecting Splits", null)
                );
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to delete detection work directory {Directory}.",
                    workDirectory
                );
            }
        }
    }

    /// <summary>
    /// Name handed to the local worker for a page. The worker writes "&lt;stem&gt;.&lt;format&gt;" into a
    /// single folder, so the name must be unique per page: two pages whose source names share a
    /// stem (e.g. "ch1/001.jpg" and "ch2/001.jpg") would otherwise overwrite each other's output
    /// before it is uploaded, storing the wrong bytes under each page's server-side output name.
    /// </summary>
    private static string WorkerPageName(int pageIndex, string sourceName) =>
        $"{pageIndex:D5}_{Path.GetFileName(sourceName)}";

    private static async Task FetchPageToFileAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        string identity,
        int pageIndex,
        string path,
        CancellationToken cancellationToken
    )
    {
        using AsyncServerStreamingCall<PageChunk> call = client.GetPages(
            new GetPagesRequest
            {
                TaskId = taskId,
                TaskIdentity = identity,
                PageIndexes = { pageIndex },
            },
            deadline: DateTime.UtcNow.Add(PageTimeout),
            cancellationToken: cancellationToken
        );

        await using FileStream file = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous
        );
        await foreach (PageChunk chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            if (chunk.PageIndex != pageIndex)
            {
                continue;
            }

            if (!chunk.Chunk.IsEmpty)
            {
                await file.WriteAsync(chunk.Chunk.Memory, cancellationToken);
            }

            if (chunk.IsLast)
            {
                break;
            }
        }
    }

    private async IAsyncEnumerable<ChapterPage> FetchPagesAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        string identity,
        IReadOnlyList<int> missingPages,
        IReadOnlyDictionary<int, string> nameByIndex,
        string sourceDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (int pageIndex in missingPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string sourceName = nameByIndex[pageIndex];
            string path = Path.Combine(sourceDirectory, WorkerPageName(pageIndex, sourceName));

            await FetchPageToFileAsync(
                client,
                taskId,
                identity,
                pageIndex,
                path,
                cancellationToken
            );

            yield return new ChapterPage(pageIndex, WorkerPageName(pageIndex, sourceName), path);
        }
    }

    private async Task UploadLoopAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        string identity,
        ChannelReader<PageUpload> uploads,
        CancellationToken stoppingToken
    )
    {
        await foreach (PageUpload upload in uploads.ReadAllAsync(stoppingToken))
        {
            using AsyncClientStreamingCall<UploadPageChunk, UploadPageResponse> call =
                client.UploadPage(
                    deadline: DateTime.UtcNow.Add(PageTimeout),
                    cancellationToken: stoppingToken
                );

            await using (FileStream file = File.OpenRead(upload.Path))
            {
                byte[] buffer = new byte[ChunkSizeBytes];
                int chunkNumber = 0;
                int bytesRead;
                while (
                    (
                        bytesRead = await file.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            stoppingToken
                        )
                    ) > 0
                )
                {
                    await call.RequestStream.WriteAsync(
                        new UploadPageChunk
                        {
                            TaskId = taskId,
                            PageIndex = upload.PageIndex,
                            ChunkNumber = chunkNumber++,
                            Chunk = ByteString.CopyFrom(buffer, 0, bytesRead),
                            ContentIdentity = identity,
                        },
                        stoppingToken
                    );
                }
            }

            await call.RequestStream.CompleteAsync();
            UploadPageResponse response = await call.ResponseAsync;
            if (!response.Success)
            {
                throw new IOException(
                    $"Uploading page {upload.PageIndex} of task {taskId} failed: {response.Message}"
                );
            }
        }
    }

    private sealed record PageUpload(int PageIndex, string Path);

    /// <summary>
    /// Forwards upscale progress as keep-alives without blocking the worker's event reader thread.
    /// </summary>
    private sealed class StreamingProgressReporter : IAsyncDisposable
    {
        private readonly Channel<UpscaleProgress> _channel = Channel.CreateBounded<UpscaleProgress>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }
        );
        private readonly ILogger<PageStreamClient> _logger;
        private readonly CancellationTokenSource _cts;
        private readonly Task _sender;

        public StreamingProgressReporter(
            UpscalingService.UpscalingServiceClient client,
            int taskId,
            CancellationToken stoppingToken,
            ILogger<PageStreamClient> logger
        )
        {
            _logger = logger;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Progress = new Progress<UpscaleProgress>(p => _channel.Writer.TryWrite(p));
            _sender = Task.Run(
                async () =>
                {
                    var debounce = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                    UpscaleProgress? pending = null;
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            while (_channel.Reader.TryRead(out UpscaleProgress? latest))
                            {
                                pending = latest;
                            }

                            if (pending is not null)
                            {
                                try
                                {
                                    await client.KeepAliveAsync(
                                        new KeepAliveRequest
                                        {
                                            TaskId = taskId,
                                            Total = pending.Total ?? 0,
                                            Current = pending.Current ?? 0,
                                            Phase = pending.Phase ?? string.Empty,
                                        },
                                        deadline: DateTime.UtcNow.AddSeconds(10),
                                        cancellationToken: _cts.Token
                                    );
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(
                                        ex,
                                        "Failed to send streaming progress for task {TaskId}.",
                                        taskId
                                    );
                                }
                            }

                            await debounce.WaitForNextTickAsync(_cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    { /* stopping */
                    }
                },
                CancellationToken.None
            );
        }

        public IProgress<UpscaleProgress> Progress { get; }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            _cts.Cancel();
            try
            {
                // Await the sender so it cannot observe a disposed token source.
                await _sender;
            }
            catch (Exception)
            { /* the sender stops on cancellation */
            }

            _cts.Dispose();
        }
    }
}
