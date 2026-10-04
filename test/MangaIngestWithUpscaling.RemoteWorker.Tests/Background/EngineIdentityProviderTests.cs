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
}
