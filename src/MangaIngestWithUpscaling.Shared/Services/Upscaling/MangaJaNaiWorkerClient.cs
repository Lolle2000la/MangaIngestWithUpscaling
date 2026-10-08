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

        IProgress<UpscaleProgress>? wrappedProgress =
            progress == null
                ? null
                : new Progress<UpscaleProgress>(p =>
                {
                    if (timeoutCts != null && timeout.HasValue && timeout.Value > TimeSpan.Zero)
                    {
                        try
                        {
                            timeoutCts.CancelAfter(timeout.Value);
                        }
                        catch (ObjectDisposedException) { }
                    }
                    progress.Report(p);
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
                    wrappedProgress,
                    effectiveToken
                );

                files.Add(new UpscaleJobFile(request.InputPath, outCbz, "success"));
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

                files.Add(new UpscaleJobFile(request.InputPath, outPath, "success"));
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

                try
                {
                    await upscaleEngine.UpscaleFileAsync(
                        page.Path,
                        outPath,
                        scale,
                        request.Format,
                        request.Quality,
                        effectiveToken
                    );

                    var jobFile = new UpscaleJobFile(page.Path, outPath, "success");
                    files.Add(jobFile);
                    onPageDone(jobFile);
                }
                catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to upscale page {PageName}", page.Name);
                    var jobFile = new UpscaleJobFile(page.Path, outPath, "error");
                    files.Add(jobFile);
                    onPageDone(jobFile);
                }

                current++;
                progress?.Report(
                    new UpscaleProgress(
                        request.TotalPages,
                        current,
                        "Upscaling",
                        $"Upscaled {page.Name}"
                    )
                );
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

        sw.Stop();
        logger.LogInformation(
            "Streamed chapter {JobId} completed in {Elapsed}s",
            request.Id,
            sw.Elapsed.TotalSeconds
        );
        return new UpscaleJobResult(request.Id, "success", files, sw.Elapsed.TotalSeconds);
    }

    public Task ShutdownWorkerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShutdownWorkerAsync(bool force, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            sessionFactory.InvalidateAllSessions();
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
