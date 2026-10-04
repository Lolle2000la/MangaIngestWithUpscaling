using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Supplies the worker's engine identities. The models, resolved backend, build and Python runtime do
/// not change while the worker runs, so the (directory-walking) computation is done once and cached.
/// The workflow config (<c>appstate2.json</c>) is the exception: it is re-read on every worker spawn,
/// so the cached upscaler identity is invalidated when its cheap fingerprint changes.
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
    private string? _upscalerWorkflowFingerprint;
    private string? _detector;

    // Compute once, but do not cache a failure: EngineIdentity does blocking file I/O (a directory
    // walk and a hash), and a transient IOException/UnauthorizedAccessException must not poison the
    // singleton for the process lifetime — every manifest and upload would then throw and be
    // classified Permanent, dropping the server spool for all tasks. The identity is also recomputed
    // when the workflow config's fingerprint changes, because MangaJaNaiWorkerSettings.EnsureSettings
    // re-reads appstate2.json on every spawn: an edit made while this process is alive would otherwise
    // run a different workflow under the old identity.
    public string Upscaler
    {
        get
        {
            lock (_lock)
            {
                string fingerprint = EngineIdentity.WorkflowConfigFingerprint();
                if (_upscaler is not null && _upscalerWorkflowFingerprint == fingerprint)
                {
                    return _upscaler;
                }

                // Hash the backend and runtime version the Python environment actually resolved to,
                // not the Auto preference: two default deployments on different hardware or with a
                // different torch/runtime must not share an identity. The environment is prepared at
                // startup, before the first task, so these are set by the time the identity is first
                // needed.
                string computed = EngineIdentity.ForUpscaler(
                    config.Value,
                    PythonService.Environment?.InstalledBackend,
                    PythonService.Environment?.EnvironmentVersion
                );

                // Only cache a success, so a transient failure is retried on the next access.
                _upscaler = computed;
                _upscalerWorkflowFingerprint = fingerprint;
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
                return _detector ??= EngineIdentity.ForDetector();
            }
        }
    }
}
