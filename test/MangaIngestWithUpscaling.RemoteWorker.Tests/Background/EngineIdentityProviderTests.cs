using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

public class EngineIdentityProviderTests
{
    /// <summary>
    /// A transient failure of the lazy computation must not be cached: the provider only stores a
    /// successful identity, so the next access recomputes instead of replaying the exception for the
    /// process lifetime (which would classify every task Permanent and drop the server spool).
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Upscaler_DoesNotCacheAFailedComputation()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "Creating symbolic links requires privileges on Windows."
        );

        string directory = Directory.CreateTempSubdirectory("engine_provider").FullName;
        try
        {
            // A broken symlink makes the directory walk throw (FileInfo.Length/File.OpenRead) without
            // relying on a permission change that a root CI would ignore.
            string model = Path.Combine(directory, "model.pth");
            File.CreateSymbolicLink(model, Path.Combine(directory, "missing-target.pth"));

            var provider = new EngineIdentityProvider(
                Options.Create(new UpscalerConfig { ModelsDirectory = directory })
            );

            Assert.ThrowsAny<IOException>(() => provider.Upscaler);

            // Resolve the failure: the second access must recompute rather than rethrow the cached one.
            File.Delete(model);
            File.WriteAllBytes(model, [1, 2, 3]);

            string identity = provider.Upscaler;

            Assert.NotEmpty(identity);
            // A successful computation is cached, so repeated access is stable.
            Assert.Equal(identity, provider.Upscaler);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="MangaJaNaiWorkerSettings.EnsureSettings"/> re-reads <c>appstate2.json</c> on every
    /// worker spawn, so an edit made while this process is alive must not be served under the old
    /// cached identity. Changing the file changes its fingerprint (length) and must force a recompute.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Upscaler_RecomputesWhenTheWorkflowConfigFingerprintChanges()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "appstate2.json");
        byte[]? original = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        string directory = Directory.CreateTempSubdirectory("engine_provider_config").FullName;
        try
        {
            var provider = new EngineIdentityProvider(
                Options.Create(new UpscalerConfig { ModelsDirectory = directory })
            );

            File.WriteAllText(configPath, "{\"workflow\":\"a\"}");
            string before = provider.Upscaler;

            // Change the content length, so the fingerprint differs even if the filesystem's write
            // timestamp granularity would not have moved.
            File.WriteAllText(configPath, "{\"workflow\":\"bb\"}");
            string after = provider.Upscaler;

            Assert.NotEqual(before, after);
            // The recomputed identity is cached again for the new fingerprint.
            Assert.Equal(after, provider.Upscaler);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            if (original is null)
            {
                File.Delete(configPath);
            }
            else
            {
                File.WriteAllBytes(configPath, original);
            }
        }
    }
}
