using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
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
    IEngineIdentityProvider engineIdentity,
    ILogger<PageStreamClient> logger
)
{
    private const int ChunkSizeBytes = 1024 * 1024;

    // The manifest normally returns immediately, but when the chapter is already fully spooled the
    // server assembles the CBZ inline before answering, so allow for a large chapter's build.
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a failed chapter's upload loop may keep draining already-produced pages before it is
    /// cancelled, so a stuck upload cannot wedge the worker.
    /// </summary>
    private static readonly TimeSpan UploadDrainGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How old a leftover stream directory must be before it is reclaimed. Generous on purpose: a
    ///     directory's own timestamp goes stale while its contents are still being written, so a short
    ///     window could delete the directory of a chapter that is streaming right now.
    /// </summary>
    private static readonly TimeSpan StaleStreamDirectoryRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Inactivity allowance for a streamed chapter, scaled by the largest page exactly like the
    /// whole-CBZ path (<c>UpscaleTimeout × max(1, maxPixels / 1e6)</c>), with a floor so slow
    /// hardware still finishes a page while a wedged worker is eventually killed.
    /// </summary>
    private TimeSpan? ChapterInactivityTimeout(long maxPagePixels)
    {
        // Match the whole-CBZ path: a non-positive UpscaleTimeout disables the inactivity kill.
        if (upscalerConfig.Value.UpscaleTimeout <= TimeSpan.Zero)
        {
            return null;
        }

        double scaling = Math.Max(1.0, maxPagePixels / 1_000_000.0);
        TimeSpan scaled = upscalerConfig.Value.UpscaleTimeout * scaling;
        TimeSpan floor = TimeSpan.FromMinutes(15);
        return scaled > floor ? scaled : floor;
    }

    public async Task RunAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        UpscalerProfile profile,
        CancellationToken stoppingToken
    )
    {
        PageManifestResponse manifest = await client.GetPageManifestAsync(
            new PageManifestRequest { TaskId = taskId, EngineIdentity = engineIdentity.Upscaler },
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

        if (manifest.TaskType != TaskType.Upscale)
        {
            // The delegation said upscale/repair but the manifest disagrees (version skew); routing
            // on the delegation alone could stream the wrong page shape. A restart preserves the
            // spool, so a rollout inconsistency does not delete a half-spooled chapter.
            throw new PageStreamRestartException(
                $"Task {taskId} was delegated as an upscale task but its manifest reports {manifest.TaskType}."
            );
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

        // Reclaim directories an earlier run left behind: a worker killed mid-chapter never reaches its
        // own cleanup, and nothing else owns these directories.
        DeleteStaleWorkDirectories();

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

            // Resolve and validate the profile before starting the upload loop: mapping the manifest
            // profile can throw (unknown enum), and it must not leave the channel uncompleted.
            // Prefer the manifest's profile: the server computes the page output names and the
            // content/engine identity from the profile resolved at manifest time, so the worker must
            // produce bytes for the same profile.
            UpscalerProfile effectiveProfile = manifest.UpscalerProfile is null
                ? profile
                : RemoteTaskProcessor.GetProfileFromResponse(manifest.UpscalerProfile);

            var chapterRequest = new ChapterJobRequest
            {
                Id = $"task-{taskId}-{Guid.NewGuid():N}",
                OutputFolder = outputDirectory,
                Format = effectiveProfile.CompressionFormat,
                Scale = effectiveProfile.ScalingFactor,
                Quality = effectiveProfile.Quality,
                // Only the missing pages are streamed, so the worker's archive_total must match, or
                // AllPagesProcessed never becomes true on a resume and the postprocess grace period is
                // never applied.
                TotalPages = missing.Count,
            };

            // Unbounded on purpose: OnPageDone is a synchronous worker callback, so it cannot apply
            // backpressure without blocking the worker's event reader. The records are tiny, but the
            // upscaled output files they point at are only deleted once uploaded, so on a fast GPU with a
            // slow link the temp footprint grows with the backlog — up to a whole extra copy of the
            // upscaled chapter before the uploads catch up.
            var uploads = Channel.CreateUnbounded<PageUpload>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
            );
            var pageErrors = new System.Collections.Concurrent.ConcurrentBag<string>();

            // The upload loop and the chapter share a token so a rejected or dropped upload stops
            // the local worker instead of letting it upscale the whole remaining chapter (and fill
            // temp disk) only to fail at the end.
            using var uploadFailureCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken
            );
            Task uploadTask = UploadLoopAsync(
                client,
                taskId,
                manifest.TaskIdentity,
                uploads.Reader,
                uploadFailureCts.Token
            );
            _ = uploadTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        uploadFailureCts.Cancel();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

            void OnPageDone(UpscaleJobFile file)
            {
                if (!indexByName.TryGetValue(file.Input, out int doneIndex))
                {
                    pageErrors.Add($"{file.Input}: {file.Status}");
                    return;
                }

                string sourcePagePath = Path.Combine(
                    sourceDirectory,
                    WorkerPageName(doneIndex, nameByIndex[doneIndex])
                );

                if (file.Status == "upscaled" && !string.IsNullOrEmpty(file.Output))
                {
                    uploads.Writer.TryWrite(
                        new PageUpload(doneIndex, file.Output, DeleteAfterUpload: true)
                    );
                    // The worker has finished this page, so its fetched source copy can go; this
                    // keeps the source directory from growing to a second copy of the chapter.
                    TryDeleteFile(sourcePagePath, doneIndex);
                }
                else
                {
                    // Match the whole-CBZ path, which copies a page the engine could not decode (or
                    // upscale) through unchanged rather than failing the chapter: upload the fetched
                    // source bytes and let the upload loop delete the file once it is sent.
                    uploads.Writer.TryWrite(
                        new PageUpload(doneIndex, sourcePagePath, DeleteAfterUpload: true)
                    );
                }
            }

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
                    uploadFailureCts.Token
                ),
                progressReporter.Progress,
                OnPageDone,
                uploadFailureCts.Token,
                timeout: ChapterInactivityTimeout(manifest.MaxPagePixels)
            );

            Exception? chapterError = null;
            try
            {
                await chapterTask;
            }
            catch (Exception ex)
            {
                chapterError = ex;
            }
            finally
            {
                uploads.Writer.TryComplete();
            }

            Exception? uploadError = null;
            try
            {
                if (chapterError is null)
                {
                    await uploadTask;
                }
                else
                {
                    // The chapter already failed: let already-produced pages upload so a resume has
                    // them, but bound the drain so a stuck upload cannot wedge the worker with the
                    // keep-alive still running.
                    await uploadTask.WaitAsync(UploadDrainGrace, CancellationToken.None);
                }
            }
            catch (TimeoutException)
            {
                // Cancel the stuck upload loop and observe its fault before the work directory is
                // torn down, so the discarded continuation cannot throw unobserved.
                await uploadFailureCts.CancelAsync();
                try
                {
                    await uploadTask;
                }
                catch (Exception)
                {
                    // The chapter error is surfaced below; the cancelled drain's fault is secondary.
                }
            }
            catch (Exception ex)
            {
                uploadError = ex;
            }

            // A failed upload cancels the chapter, so the chapter's error is then a consequential
            // cancellation; surface the real (non-cancellation) error first. A *permanent* upload
            // failure must win over a transient chapter error (for example a worker crash), or the
            // transient error would be reported instead and the task would requeue forever with the
            // spool intact instead of failing. A cancellation is never "permanent": treating it as
            // such here would rethrow the consequential cancellation and mask the real chapter error,
            // which the streaming loop then swallows as a normal interruption.
            if (
                uploadError is not null
                && uploadError is not OperationCanceledException
                && RemoteTaskProcessor.ClassifyStreamingFailure(uploadError)
                    is RemoteTaskProcessor.StreamingFailureKind.Permanent
            )
            {
                ExceptionDispatchInfo.Capture(uploadError).Throw();
            }

            if (chapterError is not null && chapterError is not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(chapterError).Throw();
            }

            if (uploadError is not null)
            {
                ExceptionDispatchInfo.Capture(uploadError).Throw();
            }

            if (chapterError is not null)
            {
                ExceptionDispatchInfo.Capture(chapterError).Throw();
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
            new PageManifestRequest { TaskId = taskId, EngineIdentity = engineIdentity.Detector },
            deadline: DateTime.UtcNow.Add(ManifestTimeout),
            cancellationToken: stoppingToken
        );

        if (manifest.Complete)
        {
            logger.LogInformation("Task {TaskId} was already fully detected.", taskId);
            return;
        }

        if (manifest.TaskType != TaskType.SplitDetection)
        {
            // A rollout inconsistency, not a permanent failure: restart so the spool survives.
            throw new PageStreamRestartException(
                $"Task {taskId} was delegated as a detection task but its manifest reports {manifest.TaskType}."
            );
        }

        if (manifest.Pages.Count == 0)
        {
            throw new InvalidOperationException($"Task {taskId} has no pages to detect.");
        }

        // Reclaim detection directories an earlier killed run left behind.
        DeleteStaleWorkDirectories();

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
            bool releasedUpscalerGpu = false;
            // One long-lived GetPages stream for the whole chapter (the server opens the source archive
            // once), yielding each page as it completes; detection reads the page as fetched, so no
            // preprocessing is applied.
            await foreach (
                ChapterPage page in FetchPagesAsync(
                    client,
                    taskId,
                    manifest.TaskIdentity,
                    missing,
                    nameByIndex,
                    sourceDirectory,
                    stoppingToken,
                    preprocess: false
                )
            )
            {
                stoppingToken.ThrowIfCancellationRequested();

                int pageIndex = page.Index;
                string sourceName = nameByIndex[pageIndex];
                string path = page.Path;

                List<SplitDetectionResult> results = await detection.DetectSplitsAsync(
                    path,
                    progressReporter.Progress,
                    stoppingToken,
                    releaseUpscalerGpu: !releasedUpscalerGpu
                );
                releasedUpscalerGpu = true;

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
                        EngineIdentity = engineIdentity.Detector,
                    },
                    deadline: DateTime.UtcNow.Add(PageTimeout),
                    cancellationToken: stoppingToken
                );
                if (!response.Success)
                {
                    // Mirror the upscale path: a non-terminal rejection means "restart", not "fail".
                    if (!response.Terminal)
                    {
                        throw new PageStreamRestartException(
                            $"The detection result for page {pageIndex} of task {taskId} was rejected non-terminally: {response.Message}"
                        );
                    }

                    throw new InvalidOperationException(
                        $"Uploading the detection result for page {pageIndex} of task {taskId} failed: {response.Message}"
                    );
                }

                // The detection is done with this page; delete its fetched copy so the detection work
                // directory does not hold the whole chapter.
                TryDeleteFile(path, pageIndex);

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

    /// <summary>
    /// Fetches all still-missing pages over one <see cref="UpscalingService.UpscalingServiceClient.GetPages"/>
    /// stream, splitting it into per-page files and yielding each page as its terminating chunk
    /// arrives. One long-lived stream keeps the server from reopening and re-enumerating the source
    /// archive once per page, while the consumer's pace still applies backpressure.
    /// </summary>
    private async IAsyncEnumerable<ChapterPage> FetchPagesAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        string identity,
        IReadOnlyList<int> missingPages,
        IReadOnlyDictionary<int, string> nameByIndex,
        string sourceDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken,
        bool preprocess = true
    )
    {
        if (missingPages.Count == 0)
        {
            yield break;
        }

        using AsyncServerStreamingCall<PageChunk> call = client.GetPages(
            new GetPagesRequest
            {
                TaskId = taskId,
                TaskIdentity = identity,
                PageIndexes = { missingPages },
            },
            deadline: DateTime.UtcNow.Add(FetchDeadline(missingPages.Count)),
            cancellationToken: cancellationToken
        );

        // Preprocess each page in place so a streamed chapter matches the whole-CBZ path, which
        // preprocesses the archive before upscaling (max dimension, format conversion, smart
        // downscale). A no-op when preprocessing is disabled. Detection reads the page as-is, so it
        // passes preprocess: false.
        IServiceScope? preprocessingScope = null;
        IImageResizeService? resizeService = null;
        ImagePreprocessingOptions? preprocessingOptions = null;
        if (preprocess && ImagePreprocessingOptions.IsEnabled(upscalerConfig.Value))
        {
            preprocessingScope = scopeFactory.CreateScope();
            resizeService =
                preprocessingScope.ServiceProvider.GetRequiredService<IImageResizeService>();
            preprocessingOptions = ImagePreprocessingOptions.FromConfig(upscalerConfig.Value);
        }

        FileStream? file = null;
        int currentIndex = -1;
        string currentName = string.Empty;
        string currentPath = string.Empty;
        long currentWritten = 0;
        var yielded = new HashSet<int>();
        try
        {
            await foreach (PageChunk chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (chunk.PageIndex != currentIndex)
                {
                    // The server always terminates a page with IsLast; a truncated page is dropped.
                    if (file is not null)
                    {
                        await file.DisposeAsync();
                        file = null;
                    }

                    currentIndex = chunk.PageIndex;
                    string sourceName = nameByIndex[currentIndex];
                    currentName = WorkerPageName(currentIndex, sourceName);
                    currentPath = Path.Combine(sourceDirectory, currentName);
                    currentWritten = 0;
                    file = new FileStream(
                        currentPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous
                    );
                }

                if (!chunk.Chunk.IsEmpty)
                {
                    await file!.WriteAsync(chunk.Chunk.Memory, cancellationToken);
                    currentWritten += chunk.Chunk.Length;
                }

                if (chunk.IsLast)
                {
                    await file!.DisposeAsync();
                    file = null;

                    // A 0-byte source entry must not be treated as a fetched page: detection would
                    // "inspect" an empty file, swallow the decode failure and finalize the chapter
                    // without ever looking at the page (the upscale path fails loudly because the
                    // server rejects a 0-byte upload).
                    if (currentWritten == 0)
                    {
                        throw new InvalidOperationException(
                            $"The server sent page {currentIndex} of task {taskId} with no data."
                        );
                    }

                    yielded.Add(currentIndex);

                    if (resizeService is not null)
                    {
                        await resizeService.PreprocessImageInPlaceAsync(
                            currentPath,
                            preprocessingOptions!,
                            cancellationToken
                        );
                    }

                    yield return new ChapterPage(currentIndex, currentName, currentPath);
                }
            }
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync();
            }

            preprocessingScope?.Dispose();
        }

        // The server silently skips a page it cannot find; if any requested page never arrived the
        // chapter can never complete, so fail loudly instead of leaving the task in Processing.
        if (yielded.Count < missingPages.Count)
        {
            IEnumerable<int> absent = missingPages.Where(index => !yielded.Contains(index));
            throw new InvalidOperationException(
                $"The server did not send page(s) {string.Join(", ", absent)} of task {taskId}."
            );
        }
    }

    /// <summary>
    /// Deadline for the single whole-chapter fetch: generously more than one page, but bounded so a
    /// stalled transfer eventually fails and is retried (resuming at the first missing page).
    /// </summary>
    private static TimeSpan FetchDeadline(int pageCount)
    {
        TimeSpan total = PageTimeout * Math.Max(1, pageCount);
        TimeSpan cap = TimeSpan.FromHours(12);
        return total < cap ? total : cap;
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

            int chunkNumber = 0;
            await using (FileStream file = File.OpenRead(upload.Path))
            {
                byte[] buffer = new byte[ChunkSizeBytes];
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
                            EngineIdentity = engineIdentity.Upscaler,
                        },
                        stoppingToken
                    );
                }
            }

            // Mark the end of the page so the server can reject a truncated upload (e.g. the worker
            // died mid-page) instead of committing a partial page as if it were whole.
            await call.RequestStream.WriteAsync(
                new UploadPageChunk
                {
                    TaskId = taskId,
                    PageIndex = upload.PageIndex,
                    ChunkNumber = chunkNumber,
                    Chunk = ByteString.Empty,
                    IsLast = true,
                    ContentIdentity = identity,
                    EngineIdentity = engineIdentity.Upscaler,
                },
                stoppingToken
            );

            await call.RequestStream.CompleteAsync();
            UploadPageResponse response = await call.ResponseAsync;
            if (!response.Success)
            {
                // A non-terminal rejection means "restart the chapter" (content/engine changed, or
                // the request hit a replica without the spool); reporting a failure would delete the
                // spool. Only a terminal rejection is a hard failure.
                if (!response.Terminal)
                {
                    throw new PageStreamRestartException(
                        $"Page {upload.PageIndex} of task {taskId} was rejected non-terminally: {response.Message}"
                    );
                }

                throw new InvalidOperationException(
                    $"Uploading page {upload.PageIndex} of task {taskId} failed: {response.Message}"
                );
            }

            if (upload.DeleteAfterUpload)
            {
                TryDeleteFile(upload.Path, upload.PageIndex);
            }
        }
    }

    /// <summary>
    ///     Removes work directories left behind by a worker that died mid-chapter. Only the two names
    ///     this client creates are considered, so no other temp content is touched.
    /// </summary>
    private void DeleteStaleWorkDirectories()
    {
        DateTime cutoff = DateTime.UtcNow - StaleStreamDirectoryRetention;
        foreach (
            string pattern in new[] { "mangaingest_page_stream_*", "mangaingest_page_detect_*" }
        )
        {
            try
            {
                foreach (
                    string directory in Directory.EnumerateDirectories(Path.GetTempPath(), pattern)
                )
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        {
                            Directory.Delete(directory, recursive: true);
                            logger.LogInformation(
                                "Removed the stale page-stream directory {Directory}.",
                                directory
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(
                            ex,
                            "Failed to remove the stale page-stream directory {Directory}.",
                            directory
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to sweep stale page-stream directories.");
            }
        }
    }

    private sealed record PageUpload(int PageIndex, string Path, bool DeleteAfterUpload);

    /// <summary>Deletes a temp file, logging (not throwing) any failure.</summary>
    private void TryDeleteFile(string path, int pageIndex)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete the fetched source page {Index}.", pageIndex);
        }
    }

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
                    using var debounce = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
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
                                    // Sent; do not re-send the same value every tick.
                                    pending = null;
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
