using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// Exercises the real <see cref="MangaJaNaiWorkerClient.RunChapterAsync"/> chapter protocol against a
/// tiny stand-in <c>worker.py</c> that speaks the NDJSON protocol. This is the runtime path the
/// static-classifier tests cannot reach: a worker that politely acknowledges the inactivity cancel
/// must still surface as a <see cref="TimeoutException"/> (recoverable) rather than as a job error
/// that the streaming classifier treats as permanent and deletes the spool for.
/// </summary>
public class MangaJaNaiWorkerClientTimeoutTests
{
    private const string FakeWorker = """
        import json
        import sys

        def emit(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        emit({"type": "ready", "capacity": 1})

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            kind = msg.get("type")
            job_id = msg.get("id")
            if kind == "open_chapter":
                emit({"type": "accepted", "id": job_id, "capacity": 1})
                emit({"type": "started", "id": job_id})
            elif kind == "cancel":
                # Honor the cancel the way the real worker does: acknowledge, then report a
                # non-"ok" done. Without the fix this faults the job and hides the timeout.
                emit({"type": "cancelled", "id": job_id})
                emit(
                    {
                        "type": "done",
                        "id": job_id,
                        "status": "cancelled",
                        "elapsed_seconds": 0.0,
                        "files": [],
                    }
                )
            elif kind == "shutdown":
                break
        """;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_WhenTheWorkerHonorsAnInactivityCancel_ThrowsTimeout()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        string workDir = Path.Combine(
            Path.GetTempPath(),
            $"mangajanai_worker_test_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(
            Path.Combine(workDir, "worker.py"),
            FakeWorker,
            TestContext.Current.CancellationToken
        );

        string pagePath = Path.Combine(workDir, "page0.png");
        await File.WriteAllBytesAsync(
            pagePath,
            [0x89, 0x50, 0x4E, 0x47],
            TestContext.Current.CancellationToken
        );

        var pythonService = Substitute.For<IPythonService>();
        pythonService
            .GetPreparedEnvironment()
            .Returns(new PythonEnvironment(python!, workDir, GpuBackend.CPU));

        await using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(pythonService)
            .BuildServiceProvider();

        var client = new MangaJaNaiWorkerClient(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig()),
            NullLogger<MangaJaNaiWorkerClient>.Instance,
            Substitute.For<IHostApplicationLifetime>()
        );

        try
        {
            var request = new ChapterJobRequest
            {
                Id = "chap-timeout",
                OutputFolder = workDir,
                Format = CompressionFormat.Webp,
                Scale = ScaleFactor.TwoX,
                TotalPages = 1,
            };

            // A short inactivity timeout: the fake worker never reports progress, so the monitor
            // fires, cancels, and must surface the timeout.
            TimeSpan timeout = TimeSpan.FromMilliseconds(500);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.RunChapterAsync(
                    request,
                    SinglePage(new ChapterPage(0, "page0.png", pagePath)),
                    progress: null,
                    onPageDone: _ => { },
                    CancellationToken.None,
                    timeout
                )
            );
        }
        finally
        {
            await client.DisposeAsync();
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static async IAsyncEnumerable<ChapterPage> SinglePage(ChapterPage page)
    {
        await Task.Yield();
        yield return page;
    }

    private static string? FindPython()
    {
        foreach (string name in new[] { "python3", "python" })
        {
            string? path = Environment
                .GetEnvironmentVariable("PATH")
                ?.Split(Path.PathSeparator)
                .Select(dir => Path.Combine(dir, name))
                .FirstOrDefault(File.Exists);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }
}
