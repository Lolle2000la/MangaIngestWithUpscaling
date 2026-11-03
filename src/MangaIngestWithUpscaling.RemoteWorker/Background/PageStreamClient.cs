using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
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

    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Deadline for the manifest call. A non-positive configured value falls back to a generous
    /// default, because the manifest can assemble an already-complete chapter inline.
    /// </summary>
    private TimeSpan ManifestDeadline() =>
        upscalerConfig.Value.ManifestTimeout > TimeSpan.Zero
            ? upscalerConfig.Value.ManifestTimeout
            : TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a failed chapter's upload loop may keep draining already-produced pages before it is
    /// cancelled, so a stuck upload cannot wedge the worker.
    /// </summary>
    private static readonly TimeSpan UploadDrainGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many finished pages may be waiting to upload before the local worker is paused. Without a
    /// bound, a fast GPU on a slow link accumulates up to a whole extra copy of the upscaled chapter on
    /// the worker's temp disk; blocking the page-done callback applies backpressure to the worker (and
    /// therefore to the fetch/upscale side), so the temp footprint is O(this), not O(chapter).
    /// </summary>
    private const int UploadBacklogLimit = 8;

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
        TimeSpan scaled = ScaledTimeout.Scale(upscalerConfig.Value.UpscaleTimeout, scaling);
        TimeSpan floor = TimeSpan.FromMinutes(15);
        return scaled > floor ? scaled : floor;
    }

    /// <summary>
    /// Resolves the preprocessing the server wants for a chapter. The server owns
    /// <c>MaxDimensionBeforeUpscaling</c> / format-conversion / smart-downscale, so its options win; an
    /// older server that does not send them makes the worker fall back to its own configuration.
    /// </summary>
    private ImagePreprocessingOptions ResolvePreprocessing(PageManifestResponse manifest)
    {
        if (string.IsNullOrEmpty(manifest.PreprocessingJson))
        {
            return ImagePreprocessingOptions.FromConfig(upscalerConfig.Value);
        }

        try
        {
            // Deserialize through the source-generated context rather than reflection-based metadata.
            return JsonSerializer.Deserialize(
                    manifest.PreprocessingJson,
                    WorkerJsonContext.Default.ImagePreprocessingOptions
                ) ?? ImagePreprocessingOptions.FromConfig(upscalerConfig.Value);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Could not parse the server's preprocessing options for task {TaskId}; using the worker's own configuration.",
                manifest.TaskId
            );
            return ImagePreprocessingOptions.FromConfig(upscalerConfig.Value);
        }
    }

    public async Task RunAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        UpscalerProfile profile,
        CancellationToken stoppingToken
    )
    {
        // Capture the engine identity once and use it for the whole stream (the manifest and every
        // upload). The provider deliberately re-probes a backend that was resolved from a transient
        // probe failure, and a session rebuild mid-chapter (a shape change, a memory descent, a
        // model change) attaches whatever provider is resolved then, so reading the identity fresh
        // for each upload would stamp pages produced by one provider with the identity of another
        // and blend them into one chapter. The capture is what keeps every page of the chapter under
        // the manifest's identity. The upload loop separately re-reads the live identity and aborts
        // through a non-terminal restart when it genuinely changed, so a real backend switch
        // re-manifests the chapter under the new identity (which resets the spool) instead of mixing
        // providers. A genuine change therefore does not wait for the next chapter: this one aborts
        // and restarts under the new identity.
        string upscalerIdentity = engineIdentity.Upscaler;

        PageManifestResponse manifest = await client.GetPageManifestAsync(
            new PageManifestRequest { TaskId = taskId, EngineIdentity = upscalerIdentity },
            deadline: DateTime.UtcNow.Add(ManifestDeadline()),
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

        // The on-disk path each fetched page was actually yielded under, keyed by manifest index.
        // Preprocessing can rename a page when a format conversion changes the extension, so the
        // yielded path is not always the worker page name reconstructed from the manifest; OnPageDone
        // deletes from here rather than reconstructing, or the converted copy would be left behind.
        var fetchedPagePaths = new System.Collections.Concurrent.ConcurrentDictionary<
            int,
            string
        >();
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
        // Owner-only at creation: these directories hold private chapter page bytes and upscaled
        // output, and Directory.CreateDirectory would honour the process umask (typically 022, i.e.
        // world-readable).
        DirectoryPermissions.CreateDirectoryRestricted(workDirectory, logger);
        DirectoryPermissions.CreateDirectoryRestricted(sourceDirectory, logger);
        DirectoryPermissions.CreateDirectoryRestricted(outputDirectory, logger);

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

            // The channel itself is unbounded, but the number of finished-but-not-yet-uploaded pages is
            // bounded by the semaphore below: OnPageDone acquires a slot before handing a page over
            // and the upload loop releases it, so at most UploadBacklogLimit pages are queued or in
            // flight. Blocking the worker's page-done callback is deliberate backpressure — without it
            // a fast GPU on a slow link would accumulate the upscaled outputs on temp disk (up to a
            // whole extra copy of the chapter) before the uploads catch up. Keeping the channel
            // unbounded means the callback only ever waits on the semaphore, never on a channel write.
            var uploads = Channel.CreateUnbounded<PageUpload>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
            );
            using var uploadSlots = new SemaphoreSlim(UploadBacklogLimit, UploadBacklogLimit);
            var pageErrors = new System.Collections.Concurrent.ConcurrentBag<string>();

            // The upload loop and the chapter share a token so a rejected or dropped upload stops
            // the local worker instead of letting it upscale the whole remaining chapter (and fill
            // temp disk) only to fail at the end.
            // Constructed before the upload loop starts: if this throws, nothing is running yet, and
            // the writer completion below (which the upload loop waits on) is guaranteed by the
            // try/finally that now covers everything from the upload loop onwards.
            await using var progressReporter = new StreamingProgressReporter(
                client,
                taskId,
                stoppingToken,
                logger
            );

            using var uploadFailureCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken
            );
            Task uploadTask = UploadLoopAsync(
                client,
                taskId,
                manifest.TaskIdentity,
                upscalerIdentity,
                uploads.Reader,
                uploadSlots,
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

            void DeleteFetchedSource(int index)
            {
                // Delete the path the fetch actually yielded. A format conversion renames the page,
                // so reconstructing the worker name points at the file preprocessing already deleted
                // and leaves the converted copy on disk until the whole work directory goes.
                if (fetchedPagePaths.TryRemove(index, out string? fetchedPath))
                {
                    TryDeleteFile(fetchedPath, index);
                }

                // Defensive: also remove the pre-conversion work name, for a page whose yielded path
                // was never recorded (and for the no-conversion case, where the two coincide).
                TryDeleteFile(
                    Path.Combine(sourceDirectory, WorkerPageName(index, nameByIndex[index])),
                    index
                );
            }

            void OnPageDone(UpscaleJobFile file)
            {
                if (!indexByName.TryGetValue(file.Input, out int doneIndex))
                {
                    pageErrors.Add($"{file.Input}: {file.Status}");
                    return;
                }

                // Block the worker's page-done callback until an upload slot frees, so a fast GPU on
                // a slow link cannot accumulate an unbounded upload backlog on disk.
                try
                {
                    uploadSlots.Wait(uploadFailureCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (file.Status == UpscaleJobStatus.Success)
                {
                    uploads.Writer.TryWrite(
                        new PageUpload(doneIndex, file.Output, DeleteAfterUpload: true)
                    );
                    // The worker has finished this page, so its fetched source copy can go; this
                    // keeps the source directory from growing to a second copy of the chapter.
                    DeleteFetchedSource(doneIndex);
                }
                else
                {
                    // A page the engine could not upscale. Record it and do NOT upload the fetched
                    // source in its place: the server assembles and marks the chapter upscaled as
                    // soon as it has received every page index, so uploading the original here is
                    // what would commit a chapter that is labelled upscaled while this page is not.
                    // Leaving the index missing fails the chapter's completeness check, and the
                    // recorded error fails the run — the whole-CBZ path fails the chapter the same
                    // way, so the two agree.
                    pageErrors.Add($"{file.Input}: {file.Status}");
                    DeleteFetchedSource(doneIndex);

                    // Released immediately, because nothing was handed to the upload loop: the slot
                    // is otherwise only freed after an upload, so every failed page would burn one
                    // permanently and the ninth failure would block this callback for good.
                    uploadSlots.Release();
                }
            }

            Exception? chapterError = null;
            try
            {
                Task chapterTask = workerClient.RunChapterAsync(
                    chapterRequest,
                    FetchPagesAsync(
                        client,
                        taskId,
                        manifest.TaskIdentity,
                        missing,
                        nameByIndex,
                        sourceDirectory,
                        uploadFailureCts.Token,
                        preprocessing: ResolvePreprocessing(manifest),
                        fetchedPaths: fetchedPagePaths
                    ),
                    progressReporter.Progress,
                    OnPageDone,
                    uploadFailureCts.Token,
                    timeout: ChapterInactivityTimeout(manifest.MaxPagePixels)
                );
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
            // which the streaming loop then swallows as a normal interruption. A cancellation is
            // therefore deferred to the last resort rather than preempting the real error — nothing is
            // swallowed, because if both errors are cancellations the final `if (chapterError ...)`
            // below still throws it.
            if (
                uploadError is not null
                && !IsCancellation(uploadError)
                && RemoteTaskProcessor.ClassifyStreamingDisposition(uploadError)
                    is PageStreamDisposition.Terminal
            )
            {
                ExceptionDispatchInfo.Capture(uploadError).Throw();
            }

            if (chapterError is not null && !IsCancellation(chapterError))
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
        // Capture once, for the same reason as the upscaler: every RPC of this stream must carry the
        // identity the manifest established, even if the provider's answer ever changes mid-stream.
        string detectorIdentity = engineIdentity.Detector;

        PageManifestResponse manifest = await client.GetPageManifestAsync(
            new PageManifestRequest { TaskId = taskId, EngineIdentity = detectorIdentity },
            deadline: DateTime.UtcNow.Add(ManifestDeadline()),
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
        // Owner-only at creation: the directory holds the fetched chapter page bytes, and
        // Directory.CreateDirectory would honour the process umask (typically 022, i.e.
        // world-readable).
        DirectoryPermissions.CreateDirectoryRestricted(workDirectory, logger);
        DirectoryPermissions.CreateDirectoryRestricted(sourceDirectory, logger);

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
                        EngineIdentity = detectorIdentity,
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
            // Hand the detector's device memory back at the chapter boundary, mirroring the upscale
            // path's chapter-boundary release. Detection caches its session in the same shared
            // OnnxSessionFactory (MaxCachedSessions == 1), so without this an idle worker keeps the
            // detector session and its execution-provider arena resident until the next inference of
            // any kind, and a following upscale plans its memory budget against a device that still
            // holds it. ReleaseGpuCacheAsync already swallows and logs its own failures, exactly as
            // the upscale finally relies on.
            await workerClient.ReleaseGpuCacheAsync(stoppingToken);

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
    /// Name handed to the local worker for a page. The name must be unique per page (the worker
    /// writes "&lt;stem&gt;.&lt;format&gt;" into a single folder, so two source names that share a stem would
    /// overwrite each other's output), and it must be a valid filename on the host OS: the server
    /// deliberately preserves legal-on-Linux names like "a:b.jpg", which Windows cannot create, and
    /// building such a path would fail the chapter permanently. The page index alone guarantees
    /// uniqueness, so use an index-only name plus a sanitized extension for decoding.
    /// </summary>
    private static string WorkerPageName(int pageIndex, string sourceName)
    {
        string extension = Path.GetExtension(sourceName);
        bool safeExtension =
            extension.Length is >= 2 and <= 10 && extension.Skip(1).All(char.IsAsciiLetterOrDigit);
        return safeExtension ? $"{pageIndex:D5}{extension}" : $"{pageIndex:D5}.img";
    }

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
        bool preprocess = true,
        ImagePreprocessingOptions? preprocessing = null,
        System.Collections.Concurrent.ConcurrentDictionary<int, string>? fetchedPaths = null
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
        // passes preprocess: false. The options come from the server (which owns the settings) when
        // it sent them, otherwise from the worker's own configuration.
        IServiceScope? preprocessingScope = null;
        IImageResizeService? resizeService = null;
        ImagePreprocessingOptions? preprocessingOptions = null;
        ImagePreprocessingOptions? effectivePreprocessing = preprocess
            ? preprocessing ?? ImagePreprocessingOptions.FromConfig(upscalerConfig.Value)
            : null;
        if (
            effectivePreprocessing is not null
            && ImagePreprocessingOptions.IsEnabled(effectivePreprocessing)
        )
        {
            preprocessingScope = scopeFactory.CreateScope();
            resizeService =
                preprocessingScope.ServiceProvider.GetRequiredService<IImageResizeService>();
            preprocessingOptions = effectivePreprocessing;
        }

        FileStream? file = null;
        int currentIndex = -1;
        string currentName = string.Empty;
        string currentPath = string.Empty;
        long currentWritten = 0;
        int currentChunkNumber = -1;
        var yielded = new HashSet<int>();
        try
        {
            await foreach (PageChunk chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (chunk.PageIndex == currentIndex && file is null)
                {
                    // A duplicate chunk for a page that already completed: its IsLast arrived, the
                    // file is closed, and the page was yielded. This server never sends one (GetPages
                    // iterates distinct indexes), but a protocol slip must not become a
                    // NullReferenceException out of the write below — that escalates as a permanent
                    // failure and drops the spool of already-fetched pages. Skip it; the next real
                    // page has a different index and reopens the stream.
                    continue;
                }

                if (chunk.PageIndex != currentIndex)
                {
                    // The server always terminates a page with IsLast; a truncated page is dropped.
                    if (file is not null)
                    {
                        await file.DisposeAsync();
                        file = null;
                    }

                    currentIndex = chunk.PageIndex;
                    // The server streams only the requested indexes, all of which are manifest pages;
                    // an index the manifest never described is a protocol slip. Look it up safely and
                    // restart rather than let KeyNotFoundException fall through the classifier to
                    // Permanent, which would make the server drop the spool of already-upscaled pages.
                    if (!nameByIndex.TryGetValue(currentIndex, out string? sourceName))
                    {
                        throw new PageStreamRestartException(
                            $"The server sent page {currentIndex} of task {taskId}, which is not in the manifest; restart the chapter."
                        );
                    }

                    currentName = WorkerPageName(currentIndex, sourceName);
                    currentPath = Path.Combine(sourceDirectory, currentName);
                    currentWritten = 0;
                    currentChunkNumber = -1;
                    file = new FileStream(
                        currentPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous
                    );
                }

                if (chunk.PageIndex == currentIndex && file is not null)
                {
                    // Chunks are numbered per page and strictly in sequence (the server increments
                    // ChunkNumber and terminates with the next one), so a repeated, skipped or
                    // reordered number means the page would be assembled from bytes that are not the
                    // prefix the server sent — a corrupt source page, which the upscaler would then
                    // commit as a successful result. Fail loudly instead: the retry re-fetches the
                    // pages, while a corrupt page is silently wrong output. The duplicate-after-
                    // completion case above is different and still skipped — there the page is
                    // already whole and yielded. The terminating chunk belongs to the sequence too,
                    // so it is checked the same way.
                    if (chunk.ChunkNumber != currentChunkNumber + 1)
                    {
                        throw new InvalidOperationException(
                            $"Chunk {chunk.ChunkNumber} of page {chunk.PageIndex} of task {taskId} is out of sequence (last accepted {currentChunkNumber}); the page cannot be assembled from a non-contiguous stream."
                        );
                    }

                    currentChunkNumber = chunk.ChunkNumber;
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

                    // The index key (currentName) and the on-disk path are deliberately different here:
                    // currentName is the worker page name the engine reports back through OnPageDone and
                    // PageStreamClient matches to a manifest index, so it must stay the name the worker
                    // was handed; the path is where the bytes actually live. Preprocessing may rename
                    // the file when a format conversion changes the extension (the converted file is
                    // moved next to the original and the original deleted), so the returned path — not
                    // currentPath — is what the engine must read, or it is handed a deleted file.
                    // Falling back to currentPath keeps the no-preprocessing and no-op cases correct.
                    string pagePath = currentPath;
                    if (resizeService is not null)
                    {
                        string preprocessedPath = await resizeService.PreprocessImageInPlaceAsync(
                            currentPath,
                            preprocessingOptions!,
                            cancellationToken
                        );
                        if (!string.IsNullOrEmpty(preprocessedPath))
                        {
                            pagePath = preprocessedPath;
                        }
                    }

                    // Record where the bytes actually landed before handing the page over, so the
                    // page-done side can delete the yielded file rather than reconstruct a name that
                    // a format conversion has already renamed away.
                    fetchedPaths?.TryAdd(currentIndex, pagePath);

                    yield return new ChapterPage(currentIndex, currentName, pagePath);
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
        // Compare the sets, not the counts: the server is only supposed to echo the requested
        // indexes, but a protocol slip that dropped one requested page while sending a different
        // manifest index instead would make yielded.Count equal missingPages.Count and let the
        // chapter finalize incomplete. The absent set is what "every requested page arrived" means.
        int[] absent = missingPages.Where(index => !yielded.Contains(index)).ToArray();
        if (absent.Length > 0)
        {
            throw new InvalidOperationException(
                $"The server did not send page(s) {string.Join(", ", absent)} of task {taskId}."
            );
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/> is a cancellation or wraps one. grpc-dotnet reports a
    /// client-side call cancellation as <see cref="RpcException"/> with
    /// <see cref="StatusCode.Cancelled"/> ("Call canceled by the client"), which is not an
    /// <see cref="OperationCanceledException"/>; the original token may survive only in the status
    /// debug text, so the status code is what is matched. Without this, the fetch's consequential
    /// cancellation (caused by an upload failure) looks like a real chapter verdict and masks the
    /// upload error the server actually reported.
    /// </summary>
    private static bool IsCancellation(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is RpcException { StatusCode: StatusCode.Cancelled })
        {
            return true;
        }

        return exception.InnerException is not null && IsCancellation(exception.InnerException);
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
        string engineIdentityValue,
        ChannelReader<PageUpload> uploads,
        SemaphoreSlim uploadSlots,
        CancellationToken stoppingToken
    )
    {
        await foreach (PageUpload upload in uploads.ReadAllAsync(stoppingToken))
        {
            // Re-read the live engine identity at the point a page is stamped for upload. The
            // identity captured for the chapter (see RunAsync) is still what every chunk carries, so
            // the server never sees a mixed chapter; but the page was produced by whichever provider
            // the session factory resolved when it ran, and the capture alone would hide that. When
            // the provider now reports a different identity, the page must not be committed into a
            // chapter manifested under the old one: stop through the same non-terminal restart the
            // server's own engine-mismatch rejection triggers, so the next manifest re-establishes
            // the identity and resets the spool rather than blending two providers into one chapter.
            string liveEngineIdentity = engineIdentity.Upscaler;
            if (!string.Equals(liveEngineIdentity, engineIdentityValue, StringComparison.Ordinal))
            {
                throw new PageStreamRestartException(
                    $"The engine identity changed mid-chapter for task {taskId}: the chapter was manifested as '{engineIdentityValue}' but the provider now reports '{liveEngineIdentity}'; restarting so the server re-manifests under the new identity."
                );
            }

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
                            EngineIdentity = engineIdentityValue,
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
                    EngineIdentity = engineIdentityValue,
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

            // Release the slot so the (possibly paused) page-done callback can hand over the next
            // page. A throwing upload faults the loop, which cancels uploadFailureCts and unblocks the
            // callback, so the slot is not needed on that path.
            uploadSlots.Release();
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
                                    var keepAlive = new KeepAliveRequest { TaskId = taskId };
                                    // proto3 optional: leave an unknown field unset (rather than
                                    // sending an explicit 0/""), so the server keeps the last known
                                    // value instead of regressing the UI to 0/total.
                                    if (pending.Total is int total)
                                    {
                                        keepAlive.Total = total;
                                    }

                                    if (pending.Current is int current)
                                    {
                                        keepAlive.Current = current;
                                    }

                                    if (pending.Phase is not null)
                                    {
                                        keepAlive.Phase = pending.Phase;
                                    }

                                    await client.KeepAliveAsync(
                                        keepAlive,
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
