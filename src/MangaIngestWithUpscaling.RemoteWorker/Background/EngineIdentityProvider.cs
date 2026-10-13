using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Supplies the worker's engine identities. The models and the build do not change while the worker
/// runs, so the (directory-walking) computation is done once and cached — the resolved backend is
/// re-checked because it is deliberately not final (a transient probe failure's CPU fallback is
/// retried, and the identity has to follow it or the server would blend pages from two backends).
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
    private GpuBackend? _upscalerBackend;
    private string? _detector;

    public string Upscaler
    {
        get
        {
            lock (_lock)
            {
                // Resolve the actual accelerator backend rather than the Auto preference:
                // two default deployments on different hardware (e.g. CUDA vs WebGPU) must not
                // share an identity or blend pages into a single chapter.
                GpuBackend effectiveBackend =
                    sessionFactory?.GetEffectiveBackend() ?? config.Value.PreferredGpuBackend;

                // Recomputed when the backend changes. GetEffectiveBackend is deliberately not
                // final: a CPU resolution produced by a transient probe failure is retried, so once
                // the driver recovers the pages run on the GPU while the memoized identity would
                // still describe CPU — the same-identity/different-pixels seam the identity exists
                // to forbid, and it silently defeated the spool reset a backend change is supposed
                // to cause. The models and build halves are unchanged by this, so the cheap path
                // (same backend) still skips the directory walk.
                if (_upscaler is not null && _upscalerBackend == effectiveBackend)
                {
                    return _upscaler;
                }

                _upscaler = EngineIdentity.ForUpscaler(config.Value, effectiveBackend);
                _upscalerBackend = effectiveBackend;
                return _upscaler;
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
