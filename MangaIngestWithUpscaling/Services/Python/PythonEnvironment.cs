namespace MangaIngestWithUpscaling.Services.Python;

public record PythonEnvironment(
    string PythonExecutablePath,
    string DesiredWorkindDirectory
    );
