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

    private static ServiceProvider BuildClient(string python, out DetectServerClient client)
    {
        string workDir = Directory.CreateTempSubdirectory("detect_server_work").FullName;
        var pythonService = Substitute.For<IPythonService>();
        pythonService
            .GetPreparedEnvironment()
            .Returns(new PythonEnvironment(python, workDir, GpuBackend.CPU));

        var provider = new ServiceCollection().AddSingleton(pythonService).BuildServiceProvider();

        client = new DetectServerClient(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(
                new UpscalerConfig { DetectServerRequestTimeout = TimeSpan.FromMinutes(1) }
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
