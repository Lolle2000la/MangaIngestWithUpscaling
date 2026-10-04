using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Supplies the worker's engine identities. The models, resolved backend, build and Python runtime do
/// not change while the worker runs, so the (directory-walking) computation is done once and cached.
/// </summary>
public interface IEngineIdentityProvider
{
    /// <summary>
    /// Identity of the upscaler (models + resolved backend + build + Python runtime), for upscale and
    /// repair tasks.
    /// </summary>
    string Upscaler { get; }

    /// <summary>Identity of the page-break detector, for split-detection tasks.</summary>
    string Detector { get; }
}

public sealed class EngineIdentityProvider(IOptions<UpscalerConfig> config)
    : IEngineIdentityProvider
{
    private readonly Lock _lock = new();
    private string? _upscaler;
    private string? _detector;

    // Compute once, but do not cache a failure: EngineIdentity does blocking file I/O (a directory
    // walk and a hash), and a transient IOException/UnauthorizedAccessException must not poison the
    // singleton for the process lifetime — every manifest and upload would then throw and be
    // classified Permanent, dropping the server spool for all tasks.
    public string Upscaler
    {
        get
        {
            lock (_lock)
            {
                // Hash the backend and runtime version the Python environment actually resolved to,
                // not the Auto preference: two default deployments on different hardware or with a
                // different torch/runtime must not share an identity. The environment is prepared at
                // startup, before the first task, so these are set by the time the identity is first
                // needed.
                return _upscaler ??= EngineIdentity.ForUpscaler(
                    config.Value,
                    PythonService.Environment?.InstalledBackend,
                    PythonService.Environment?.EnvironmentVersion
                );
            }
        }
    }

    public string Detector
    {
        get
        {
            lock (_lock)
            {
                return _detector ??= EngineIdentity.ForDetector();
            }
        }
    }
}
