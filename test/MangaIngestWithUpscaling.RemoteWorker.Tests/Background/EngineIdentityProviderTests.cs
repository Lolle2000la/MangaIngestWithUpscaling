using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Options;
using NSubstitute;
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
            string model = Path.Combine(directory, "model.onnx");
            File.CreateSymbolicLink(model, Path.Combine(directory, "missing-target.onnx"));

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
    /// When PreferredGpuBackend is Auto, two workers on different hardware must produce different
    /// engine identities according to their resolved backend (e.g. CUDA vs WebGPU) to prevent
    /// blending pages from different accelerators into a single chapter.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Upscaler_DifferentiatesResolvedBackends()
    {
        string directory = Directory.CreateTempSubdirectory("engine_provider_config").FullName;
        try
        {
            var mockFactoryCuda =
                NSubstitute.Substitute.For<MangaIngestWithUpscaling.Shared.Services.Inference.IOnnxSessionFactory>();
            mockFactoryCuda.GetEffectiveBackend().Returns(GpuBackend.CUDA);

            var mockFactoryWebGpu =
                NSubstitute.Substitute.For<MangaIngestWithUpscaling.Shared.Services.Inference.IOnnxSessionFactory>();
            mockFactoryWebGpu.GetEffectiveBackend().Returns(GpuBackend.WebGPU);

            var options = Options.Create(
                new UpscalerConfig
                {
                    ModelsDirectory = directory,
                    PreferredGpuBackend = GpuBackend.Auto,
                }
            );

            var providerCuda = new EngineIdentityProvider(options, mockFactoryCuda);
            var providerWebGpu = new EngineIdentityProvider(options, mockFactoryWebGpu);

            string identityCuda = providerCuda.Upscaler;
            string identityWebGpu = providerWebGpu.Upscaler;

            Assert.NotEqual(identityCuda, identityWebGpu);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The identity is recomputed when the resolved backend changes. GetEffectiveBackend is
    /// deliberately not final — a CPU resolution produced by a transient probe failure is retried
    /// every 60 s — so memoizing the identity forever let pages run on the recovered GPU under an
    /// identity that still described CPU: the same-identity/different-pixels seam the identity exists
    /// to forbid, and it silently defeated the spool reset a genuine backend change must cause.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Upscaler_FollowsAResolvedBackendChange()
    {
        string directory = Directory.CreateTempSubdirectory("engine_provider_backend").FullName;
        try
        {
            var factory = Substitute.For<IOnnxSessionFactory>();
            factory.GetEffectiveBackend().Returns(GpuBackend.CPU);

            var options = Options.Create(
                new UpscalerConfig
                {
                    ModelsDirectory = directory,
                    PreferredGpuBackend = GpuBackend.Auto,
                }
            );
            var provider = new EngineIdentityProvider(options, factory);

            string unrecovered = provider.Upscaler;

            // The same backend stays cached, so the directory walk is not repeated per page.
            Assert.Equal(unrecovered, provider.Upscaler);

            factory.GetEffectiveBackend().Returns(GpuBackend.WebGPU);
            string recovered = provider.Upscaler;

            Assert.NotEqual(unrecovered, recovered);
            Assert.Equal(recovered, provider.Upscaler);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
