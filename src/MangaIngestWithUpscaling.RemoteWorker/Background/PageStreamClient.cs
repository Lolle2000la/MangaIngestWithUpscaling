using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
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
    ILogger<PageStreamClient> logger
)
{
    private const int ChunkSizeBytes = 1024 * 1024;
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(10);

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

        if (manifest.Pages.Count == 0)
        {
            throw new InvalidOperationException($"Task {taskId} has no pages to upscale.");
        }

        if (manifest.Complete)
        {
            logger.LogInformation(
                "Task {TaskId} was already fully spooled; the server assembled it.",
                taskId
            );
            return;
        }

        Dictionary<int, string> nameByIndex = manifest.Pages.ToDictionary(
            p => p.Index,
            p => p.SourceName
        );
        Dictionary<string, int> indexByName = manifest.Pages.ToDictionary(
            p => p.SourceName,
            p => p.Index
        );
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

            using var progressReporter = new StreamingProgressReporter(
                client,
                taskId,
                stoppingToken,
                logger
            );

            try
            {
                await workerClient.RunChapterAsync(
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
                    timeout: null
                );
            }
            finally
            {
                uploads.Writer.TryComplete();
                await uploadTask;
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
            string path = Path.Combine(
                sourceDirectory,
                $"{pageIndex:D5}_{Path.GetFileName(sourceName)}"
            );

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

            await using (
                FileStream file = new(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous
                )
            )
            {
                await foreach (
                    PageChunk chunk in call.ResponseStream.ReadAllAsync(cancellationToken)
                )
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

            yield return new ChapterPage(pageIndex, sourceName, path);
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
    private sealed class StreamingProgressReporter : IDisposable
    {
        private readonly Channel<UpscaleProgress> _channel = Channel.CreateBounded<UpscaleProgress>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }
        );
        private readonly ILogger<PageStreamClient> _logger;
        private readonly Task _sender;

        public StreamingProgressReporter(
            UpscalingService.UpscalingServiceClient client,
            int taskId,
            CancellationToken stoppingToken,
            ILogger<PageStreamClient> logger
        )
        {
            _logger = logger;
            Progress = new Progress<UpscaleProgress>(p => _channel.Writer.TryWrite(p));
            _sender = Task.Run(
                async () =>
                {
                    var debounce = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                    UpscaleProgress? pending = null;
                    try
                    {
                        while (!stoppingToken.IsCancellationRequested)
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
                                        cancellationToken: stoppingToken
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

                            await debounce.WaitForNextTickAsync(stoppingToken);
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

        public void Dispose()
        {
            _channel.Writer.TryComplete();
        }
    }
}
