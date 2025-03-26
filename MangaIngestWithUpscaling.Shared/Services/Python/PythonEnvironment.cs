namespace MangaIngestWithUpscaling.Shared.Services.Python;

public record PythonEnvironment(
    string PythonExecutablePath,
    string DesiredWorkindDirectory
    );