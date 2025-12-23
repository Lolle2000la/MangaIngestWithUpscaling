using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.Shared.Services.Python;

public record PythonEnvironment(
    string PythonExecutablePath,
    string DesiredWorkindDirectory,
    GpuBackend InstalledBackend
);
