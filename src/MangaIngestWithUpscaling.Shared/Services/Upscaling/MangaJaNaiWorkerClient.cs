using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterSingleton]
public class MangaJaNaiWorkerClient(
    IOnnxUpscaleEngine upscaleEngine,
    ILogger<MangaJaNaiWorkerClient> logger
) : IMangaJaNaiWorkerClient, IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        int scale = (int)request.Scale;
        List<UpscaleJobFile> files = [];

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
                progress,
                cancellationToken
            );

            files.Add(new UpscaleJobFile(request.InputPath, outCbz, "success"));
        }
        else if (File.Exists(request.InputPath))
        {
            string targetExt = ToExtension(request.Format);
            string outFileName = Path.GetFileNameWithoutExtension(request.InputPath) + targetExt;
            string outPath = Path.Combine(request.OutputFolder, outFileName);

            await upscaleEngine.UpscaleFileAsync(
                request.InputPath,
                outPath,
                scale,
                request.Format,
                request.Quality,
                cancellationToken
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

        int scale = (int)request.Scale;
        string targetExt = ToExtension(request.Format);
        List<UpscaleJobFile> files = [];
        int current = 0;

        await foreach (var page in pages.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    cancellationToken
                );

                var jobFile = new UpscaleJobFile(page.Path, outPath, "success");
                files.Add(jobFile);
                onPageDone(jobFile);
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

    public Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

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
