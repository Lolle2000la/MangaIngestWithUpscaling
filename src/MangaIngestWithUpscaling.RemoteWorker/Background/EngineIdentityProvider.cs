using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Supplies the worker's engine identities. The models, resolved backend and build do
/// not change while the worker runs, so the (directory-walking) computation is done once and cached.
/// </summary>
public interface IEngineIdentityProvider
{
    /// <summary>
    /// Identity of the upscaler (models + resolved backend + build), for upscale and repair tasks.
    /// </summary>
    string Upscaler { get; }

    /// <summary>Identity of the page-break detector, for split-detection tasks.</summary>
    string Detector { get; }
}

public sealed class EngineIdentityProvider(
    IOptions<UpscalerConfig> config,
    IOnnxSessionFactory? sessionFactory = null
) : IEngineIdentityProvider
{
    private readonly Lock _lock = new();
    private string? _upscaler;
    private string? _detector;

    public string Upscaler
    {
        get
        {
            lock (_lock)
            {
                if (_upscaler is not null)
                {
                    return _upscaler;
                }

                // Resolve the actual accelerator backend rather than the Auto preference:
                // two default deployments on different hardware (e.g. CUDA vs WebGPU) must not
                // share an identity or blend pages into a single chapter.
                GpuBackend effectiveBackend =
                    sessionFactory?.GetEffectiveBackend() ?? config.Value.PreferredGpuBackend;

                string computed = EngineIdentity.ForUpscaler(config.Value, effectiveBackend);

                _upscaler = computed;
                return computed;
            }
        }
    }

    public string Detector
    {
        get
        {
            lock (_lock)
            {
                return _detector ??= EngineIdentity.ForDetector(
                    config.Value.ResolvedModelsDirectory
                );
            }
        }
    }
}
