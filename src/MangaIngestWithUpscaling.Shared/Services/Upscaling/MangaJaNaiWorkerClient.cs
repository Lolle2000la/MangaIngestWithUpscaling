using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterSingleton]
public class MangaJaNaiWorkerClient(
    IOnnxUpscaleEngine upscaleEngine,
    IOnnxSessionFactory sessionFactory,
    ILogger<MangaJaNaiWorkerClient> logger
) : IMangaJaNaiWorkerClient
{
    public async Task<UpscaleJobResult> RunJobAsync(
        UpscaleJobRequest request,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    )
    {
        logger.LogInformation(
            "Starting upscale job {JobId} for {InputPath}",
            request.Id,
            request.InputPath
        );
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using CancellationTokenSource? timeoutCts =
            timeout.HasValue && timeout.Value > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(timeout!.Value);
        }

        CancellationToken effectiveToken = timeoutCts?.Token ?? cancellationToken;

        // The inactivity clock has to be reset by the work itself, not by whether the caller happens
        // to want progress. Wrapping only the caller's sink left a job submitted without one running
        // on a hard wall-clock timeout, even though the CBZ path reports a page at a time — the exact
        // thing the timeout is documented not to do.
        var resetProgress = new Progress<UpscaleProgress>(p =>
        {
            if (timeoutCts != null && timeout.HasValue && timeout.Value > TimeSpan.Zero)
            {
                try
                {
                    timeoutCts.CancelAfter(timeout.Value);
                }
                catch (ObjectDisposedException) { }
            }

            progress?.Report(p);
        });

        int scale = (int)request.Scale;
        List<UpscaleJobFile> files = [];

        try
        {
            if (request.InputPath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
            {
                string filename = request.OutputFilename;
                if (filename.Contains("%filename%"))
                {
                    filename = filename.Replace(
                        "%filename%",
                        Path.GetFileNameWithoutExtension(request.InputPath)
                    );
                }
                if (!filename.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
                {
                    filename += ".cbz";
                }
                string outCbz = Path.Combine(request.OutputFolder, filename);

                await upscaleEngine.UpscaleCbzAsync(
                    request.InputPath,
                    outCbz,
                    scale,
                    request.Format,
                    request.Quality,
                    resetProgress,
                    effectiveToken
                );

                files.Add(new UpscaleJobFile(request.InputPath, outCbz, UpscaleJobStatus.Success));
            }
            else if (File.Exists(request.InputPath))
            {
                string targetExt = ToExtension(request.Format);
                string outFileName =
                    Path.GetFileNameWithoutExtension(request.InputPath) + targetExt;
                string outPath = Path.Combine(request.OutputFolder, outFileName);

                await upscaleEngine.UpscaleFileAsync(
                    request.InputPath,
                    outPath,
                    scale,
                    request.Format,
                    request.Quality,
                    effectiveToken
                );

                files.Add(new UpscaleJobFile(request.InputPath, outPath, UpscaleJobStatus.Success));
            }
            else
            {
                throw new FileNotFoundException(
                    $"Input path not found: {request.InputPath}",
                    request.InputPath
                );
            }
        }
        catch (OperationCanceledException)
            when (timeoutCts?.IsCancellationRequested == true
                && !cancellationToken.IsCancellationRequested
            )
        {
            logger.LogError(
                "Upscale job {JobId} exceeded inactivity timeout of {Timeout}",
                request.Id,
                timeout!.Value
            );
            throw new TimeoutException(
                $"Upscale job {request.Id} timed out after {timeout.Value} of inactivity."
            );
        }

        sw.Stop();
        logger.LogInformation(
            "Upscale job {JobId} completed in {Elapsed}s",
            request.Id,
            sw.Elapsed.TotalSeconds
        );
        return new UpscaleJobResult(request.Id, "success", files, sw.Elapsed.TotalSeconds);
    }

    public async Task<UpscaleJobResult> RunChapterAsync(
        ChapterJobRequest request,
        IAsyncEnumerable<ChapterPage> pages,
        IProgress<UpscaleProgress>? progress,
        Action<UpscaleJobFile> onPageDone,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    )
    {
        logger.LogInformation("Starting streamed chapter {JobId}", request.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using CancellationTokenSource? timeoutCts =
            timeout.HasValue && timeout.Value > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(timeout!.Value);
        }

        CancellationToken effectiveToken = timeoutCts?.Token ?? cancellationToken;

        int scale = (int)request.Scale;
        string targetExt = ToExtension(request.Format);
        List<UpscaleJobFile> files = [];
        int current = 0;

        try
        {
            // Pipelined: page N-1 finishes encoding and is reported done while page N runs on the GPU.
            (ChapterPage Page, string OutPath, Task Write)? pending = null;

            void RecordPage(UpscaleJobFile jobFile, string pageName)
            {
                files.Add(jobFile);
                onPageDone(jobFile);
                current++;
                progress?.Report(
                    new UpscaleProgress(
                        request.TotalPages,
                        current,
                        "Upscaling",
                        $"Upscaled {pageName}"
                    )
                );
            }

            async Task FinalizePendingAsync()
            {
                if (pending is not { } p)
                {
                    return;
                }
                pending = null;

                UpscaleJobFile jobFile;
                try
                {
                    await p.Write;
                    // The streamed consumer (PageStreamClient) keys its per-page index by the worker
                    // page name (ChapterPage.Name), not the local temp path. Report the name so a
                    // finished page maps back to its manifest index; reporting p.Page.Path makes
                    // every page an unmatched error, so nothing uploads and the chapter fails.
                    jobFile = new UpscaleJobFile(p.Page.Name, p.OutPath, UpscaleJobStatus.Success);
                }
                catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!IsInfrastructureFailure(ex))
                {
                    logger.LogError(ex, "Failed to upscale page {PageName}", p.Page.Name);
                    jobFile = new UpscaleJobFile(p.Page.Name, p.OutPath, UpscaleJobStatus.Error);
                }
                RecordPage(jobFile, p.Page.Name);
            }

            try
            {
                await foreach (var page in pages.WithCancellation(effectiveToken))
                {
                    effectiveToken.ThrowIfCancellationRequested();

                    if (timeoutCts != null && timeout.HasValue && timeout.Value > TimeSpan.Zero)
                    {
                        try
                        {
                            timeoutCts.CancelAfter(timeout.Value);
                        }
                        catch (ObjectDisposedException) { }
                    }

                    string outFileName = Path.GetFileNameWithoutExtension(page.Name) + targetExt;
                    string outPath = Path.Combine(request.OutputFolder, outFileName);

                    progress?.Report(
                        new UpscaleProgress(
                            request.TotalPages,
                            current,
                            "Upscaling",
                            $"Upscaling {page.Name}"
                        )
                    );

                    Task<Task> inference = upscaleEngine.UpscaleFileStagedAsync(
                        page.Path,
                        outPath,
                        scale,
                        request.Format,
                        request.Quality,
                        effectiveToken
                    );

                    try
                    {
                        await FinalizePendingAsync();
                    }
                    catch
                    {
                        // Don't leave inference or an encode running behind a failed/cancelled job.
                        try
                        {
                            await await inference;
                        }
                        catch { }
                        throw;
                    }

                    try
                    {
                        pending = (page, outPath, await inference);
                    }
                    catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (!IsInfrastructureFailure(ex))
                    {
                        logger.LogError(ex, "Failed to upscale page {PageName}", page.Name);
                        RecordPage(
                            new UpscaleJobFile(page.Name, outPath, UpscaleJobStatus.Error),
                            page.Name
                        );
                    }
                }

                await FinalizePendingAsync();
            }
            finally
            {
                if (pending is { } leftover)
                {
                    pending = null;
                    try
                    {
                        await leftover.Write;
                    }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException)
            when (timeoutCts?.IsCancellationRequested == true
                && !cancellationToken.IsCancellationRequested
            )
        {
            logger.LogError(
                "Streamed chapter {JobId} exceeded inactivity timeout of {Timeout}",
                request.Id,
                timeout!.Value
            );
            throw new TimeoutException(
                $"Streamed chapter {request.Id} timed out after {timeout.Value} of inactivity."
            );
        }
        finally
        {
            // Hand the device memory back at the chapter boundary whether the chapter succeeded, failed
            // or timed out. The whole-CBZ path does the same after a chapter; without it here, a worker
            // that streams chapter after chapter keeps every buffer the execution provider grew for the
            // last one, and the budget the next chapter is planned against is no longer what it thinks
            // it is. It also has to happen on the failure path, or a chapter that failed would hold the
            // session into its own retry.
            await ReleaseGpuCacheAsync(effectiveToken);
        }

        sw.Stop();
        logger.LogInformation(
            "Streamed chapter {JobId} completed in {Elapsed}s",
            request.Id,
            sw.Elapsed.TotalSeconds
        );

        return new UpscaleJobResult(request.Id, "success", files, sw.Elapsed.TotalSeconds);
    }

    public Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Synchronous, not InvalidateAllSessions: the latter only retires the session and lets a
            // background drain dispose it once no inference scope holds it, so a chapter boundary
            // would hand back nothing — and the next chapter would plan its budget against memory
            // that is still held. This is the contract IOnnxSessionFactory documents for a chapter
            // boundary, and no scope is held here. (It also waits out a scope on another thread
            // rather than deadlocking: the caller holds none.)
            sessionFactory.ReleaseCachedSessions();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanly release GPU session cache.");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// True for failures that say something about the machine, not about this one page. They must
    /// propagate rather than being turned into a per-page error: a device hiccup or a full disk is
    /// classified Transient upstream precisely so the worker is requeued, and swallowing it here
    /// would commit a chapter whose page was never upscaled.
    /// </summary>
    private static bool IsInfrastructureFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException || OnnxTiler.IsMemoryException(ex);

    public static string ToExtension(CompressionFormat format) =>
        format switch
        {
            CompressionFormat.Avif => ".avif",
            CompressionFormat.Webp => ".webp",
            CompressionFormat.Jpg => ".jpg",
            CompressionFormat.Png => ".png",
            _ => ".webp",
        };

    public static string ToFormatString(CompressionFormat format) =>
        format switch
        {
            CompressionFormat.Avif => "avif",
            CompressionFormat.Webp => "webp",
            CompressionFormat.Jpg => "jpeg",
            CompressionFormat.Png => "png",
            _ => "webp",
        };
}
