using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

/// <summary>
/// Exercises the real <see cref="DetectServerClient"/> process lifecycle against a tiny stand-in
/// <c>detect_server.py</c> that speaks the NDJSON protocol: spawn + ready, a detection round-trip,
/// the GPU-cache release, a clean shutdown, and the startup-failure path (a model-load traceback).
///
/// The layout root is a process-wide static, so this collection runs serially.
/// </summary>
[Collection("DetectServerClientLayout")]
public class DetectServerClientTests
{
    private const string FakeServer = """
        import json
        import sys

        def emit(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        emit({"type": "ready", "device": "cpu"})

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            kind = msg.get("type")
            if kind == "detect":
                emit(
                    {
                        "type": "result",
                        "id": msg.get("id"),
                        "result": {
                            "image": msg.get("path") or "",
                            "splits": [],
                            "count": 0,
                        },
                    }
                )
            elif kind == "release_cache":
                emit({"type": "cache_released", "status": "ok"})
            elif kind == "shutdown":
                break
        """;

    private const string FailingServer = """
        import sys
        print("Failed to load model: simulated traceback", file=sys.stderr)
        sys.exit(1)
        """;

    [Fact]
    [Trait("Category", "Unit")]
    public void StdinEncoding_DoesNotEmitABom()
    {
        // The resident detection server does json.loads(line), which rejects a leading BOM; a BOM
        // would make every request fail to parse and stall for the full request timeout.
        Assert.Empty(DetectServerClient.StdinEncoding.GetPreamble());

        // The encoding must still round-trip non-ASCII paths.
        byte[] bytes = DetectServerClient.StdinEncoding.GetBytes("{\"path\":\"ページ.png\"}");
        Assert.Equal(
            "{\"path\":\"ページ.png\"}",
            DetectServerClient.StdinEncoding.GetString(bytes)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectAsync_SpawnsTheServerDetectsAndReleasesTheCache()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        using var layout = new LayoutScope(FakeServer);
        using var host = BuildClient(python!, out DetectServerClient client);
        string image = CreateTempImage();
        try
        {
            SplitDetectionResult result = await client.DetectAsync(
                image,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(image, result.ImagePath);
            Assert.Empty(result.Splits);
            Assert.True(await client.ReleaseGpuCacheAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await client.ShutdownServerAsync(TestContext.Current.CancellationToken);
            File.Delete(image);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectAsync_SurfacesTheStartupFailureCause()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        using var layout = new LayoutScope(FailingServer);
        using var host = BuildClient(python!, out DetectServerClient client);
        string image = CreateTempImage();
        try
        {
            DetectServerUnavailableException error =
                await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                    client.DetectAsync(image, TestContext.Current.CancellationToken)
                );

            // The model-load traceback must reach the caller, not be swallowed at Debug level.
            Assert.Contains("Failed to load model", error.Message);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectAsync_RemembersAStartupFailureInsteadOfRespawningPerRequest()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        string starts = Path.Combine(Path.GetTempPath(), $"detect_starts_{Guid.NewGuid():N}.txt");
        using var layout = new LayoutScope(CountingFailingServer(starts));
        using var host = BuildClient(python!, out DetectServerClient client);
        string image = CreateTempImage();
        try
        {
            DetectServerUnavailableException first =
                await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                    client.DetectAsync(image, TestContext.Current.CancellationToken)
                );
            Assert.Contains("Failed to load model", first.Message);

            // The next request must answer from the remembered failure: on a host where the model never
            // loads in time, re-attempting per page would spend a full ready timeout before every page's
            // CLI fallback (a startup that never becomes ready fails through this same path).
            DetectServerUnavailableException second =
                await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                    client.DetectAsync(image, TestContext.Current.CancellationToken)
                );
            Assert.Contains("not retried until", second.Message);

            // The proof that it did not retry: the stand-in server was started exactly once.
            Assert.Single(File.ReadAllLines(starts));
        }
        finally
        {
            File.Delete(image);
            File.Delete(starts);
        }
    }

    /// <summary>Failing server that records every start, so a retry would be visible.</summary>
    private static string CountingFailingServer(string startsPath) =>
        $$"""
            import sys

            with open(r"{{startsPath}}", "a", encoding="utf-8") as handle:
                handle.write("start\n")
            print("Failed to load model: simulated traceback", file=sys.stderr)
            sys.exit(1)
            """;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectAsync_RemembersARequestTimeoutInsteadOfRespawningPerPage()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        string starts = Path.Combine(Path.GetTempPath(), $"detect_starts_{Guid.NewGuid():N}.txt");
        using var layout = new LayoutScope(CountingBlockingServer(starts));
        using var host = BuildClient(
            python!,
            out DetectServerClient client,
            TimeSpan.FromSeconds(1)
        );
        string image = CreateTempImage();
        try
        {
            DetectServerUnavailableException first =
                await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                    client.DetectAsync(image, TestContext.Current.CancellationToken)
                );
            Assert.Contains("timed out", first.Message);

            // A detector that hangs on every page must not pay a fresh spawn plus a full request
            // timeout per page: the timeout is remembered, so the next request answers from it.
            DetectServerUnavailableException second =
                await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                    client.DetectAsync(image, TestContext.Current.CancellationToken)
                );
            Assert.Contains("not retried until", second.Message);
            Assert.Single(File.ReadAllLines(starts));
        }
        finally
        {
            File.Delete(image);
            File.Delete(starts);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectAsync_RemembersAPostReadyCrashInsteadOfRespawningPerPage()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        string starts = Path.Combine(Path.GetTempPath(), $"detect_starts_{Guid.NewGuid():N}.txt");
        using var layout = new LayoutScope(CountingCrashingServer(starts));
        using var host = BuildClient(python!, out DetectServerClient client);
        string image = CreateTempImage();
        try
        {
            await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                client.DetectAsync(image, TestContext.Current.CancellationToken)
            );

            // The crash is remembered, so a detector that dies on every page does not pay a fresh
            // spawn per page.
            DetectServerUnavailableException second =
                await Assert.ThrowsAsync<DetectServerUnavailableException>(() =>
                    client.DetectAsync(image, TestContext.Current.CancellationToken)
                );
            Assert.Contains("not retried until", second.Message);
            Assert.Single(File.ReadAllLines(starts));
        }
        finally
        {
            File.Delete(image);
            File.Delete(starts);
        }
    }

    /// <summary>Server that records every start and then wedges on every detection.</summary>
    private static string CountingBlockingServer(string startsPath) =>
        $$"""
            import json
            import sys
            import time

            with open(r"{{startsPath}}", "a", encoding="utf-8") as handle:
                handle.write("start\n")

            def emit(obj):
                sys.stdout.write(json.dumps(obj) + "\n")
                sys.stdout.flush()

            emit({"type": "ready", "device": "cpu"})

            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except ValueError:
                    continue
                if msg.get("type") == "detect":
                    while True:
                        time.sleep(1)
            """;

    /// <summary>Server that records every start, becomes ready, and then exits immediately.</summary>
    private static string CountingCrashingServer(string startsPath) =>
        $$"""
            import json
            import sys

            with open(r"{{startsPath}}", "a", encoding="utf-8") as handle:
                handle.write("start\n")

            sys.stdout.write(json.dumps({"type": "ready", "device": "cpu"}) + "\n")
            sys.stdout.flush()
            sys.exit(1)
            """;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectAsync_KillsAWedgedServerWhenTheRequestIsCancelled()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "Python 3 is not available on this machine.");

        string blockFlag = Path.Combine(Path.GetTempPath(), $"detect_block_{Guid.NewGuid():N}");
        File.WriteAllText(blockFlag, "block");
        using var layout = new LayoutScope(BlockingServer(blockFlag));
        using var host = BuildClient(
            python!,
            out DetectServerClient client,
            TimeSpan.FromSeconds(5)
        );
        string image = CreateTempImage();
        try
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.DetectAsync(image, cancelled.Token)
            );

            // The detector answers one request at a time and the first one is wedged for good, so the
            // next request can only succeed if the cancel killed that server and this call spawned a
            // fresh one (the stand-in consumes the marker when it wedges).
            SplitDetectionResult result = await client.DetectAsync(
                image,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(image, result.ImagePath);
        }
        finally
        {
            await client.ShutdownServerAsync(TestContext.Current.CancellationToken);
            File.Delete(image);
            File.Delete(blockFlag);
        }
    }

    /// <summary>
    ///     Stand-in server that wedges on a detection while <paramref name="blockFlagPath" /> exists, so the
    ///     cancel arrives while it cannot even read stdin — the case that used to lock the next detection
    ///     out until its own request timeout.
    /// </summary>
    private static string BlockingServer(string blockFlagPath) =>
        $$"""
            import json
            import os
            import sys
            import time

            def emit(obj):
                sys.stdout.write(json.dumps(obj) + "\n")
                sys.stdout.flush()

            emit({"type": "ready", "device": "cpu"})

            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except ValueError:
                    continue
                if msg.get("type") != "detect":
                    continue
                if os.path.exists(r"{{blockFlagPath}}"):
                    # Wedge this process for good, and consume the marker so a replacement server answers.
                    os.remove(r"{{blockFlagPath}}")
                    while True:
                        time.sleep(1)
                emit(
                    {
                        "type": "result",
                        "id": msg.get("id"),
                        "result": {"image": msg.get("path") or "", "splits": [], "count": 0},
                    }
                )
            """;

    private static ServiceProvider BuildClient(
        string python,
        out DetectServerClient client,
        TimeSpan? requestTimeout = null
    )
    {
        string workDir = Directory.CreateTempSubdirectory("detect_server_work").FullName;
        var pythonService = Substitute.For<IPythonService>();
        pythonService
            .GetPreparedEnvironment()
            .Returns(new PythonEnvironment(python, workDir, GpuBackend.CPU, 15));

        var provider = new ServiceCollection().AddSingleton(pythonService).BuildServiceProvider();

        client = new DetectServerClient(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(
                new UpscalerConfig
                {
                    DetectServerRequestTimeout = requestTimeout ?? TimeSpan.FromMinutes(1),
                }
            ),
            NullLogger<DetectServerClient>.Instance,
            Substitute.For<IHostApplicationLifetime>()
        );
        return provider;
    }

    private static string CreateTempImage()
    {
        string image = Path.Combine(Path.GetTempPath(), $"detect_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(image, [1, 2, 3]);
        return image;
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

    /// <summary>
    /// Points <see cref="SplitDetectionLayout.Root"/> at a temp directory containing the fake server
    /// script, checkpoint and config, and restores it on dispose.
    /// </summary>
    private sealed class LayoutScope : IDisposable
    {
        private readonly string _previousRoot = SplitDetectionLayout.Root;
        private readonly string _root;

        public LayoutScope(string serverSource)
        {
            _root = Directory.CreateTempSubdirectory("detect_layout").FullName;
            SplitDetectionLayout.Root = _root;

            string script = SplitDetectionLayout.ServerScriptPath;
            string checkpoint = SplitDetectionLayout.CheckpointPath;
            string config = SplitDetectionLayout.ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
            File.WriteAllText(script, serverSource);
            File.WriteAllText(checkpoint, "model");
            File.WriteAllText(config, "{}");
        }

        public void Dispose()
        {
            SplitDetectionLayout.Root = _previousRoot;
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { }
        }
    }
}

[CollectionDefinition("DetectServerClientLayout", DisableParallelization = true)]
public class DetectServerClientLayoutCollection;
