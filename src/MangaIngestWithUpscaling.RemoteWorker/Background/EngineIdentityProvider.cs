using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Supplies the worker's engine identities. The models and preprocessing configuration do not change
/// while the worker runs, so the (directory-walking) computation is done once and cached.
/// </summary>
public interface IEngineIdentityProvider
{
    /// <summary>Identity of the upscaler (models + preprocessing), for upscale and repair tasks.</summary>
    string Upscaler { get; }

    /// <summary>Identity of the page-break detector, for split-detection tasks.</summary>
    string Detector { get; }
}

public sealed class EngineIdentityProvider(IOptions<UpscalerConfig> config)
    : IEngineIdentityProvider
{
    private readonly Lazy<string> _upscaler = new(() => EngineIdentity.ForUpscaler(config.Value));
    private readonly Lazy<string> _detector = new(EngineIdentity.ForDetector);

    public string Upscaler => _upscaler.Value;

    public string Detector => _detector.Value;
}
