namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

/// <summary>
/// Filesystem layout of the bundled page-break detector, relative to the application base
/// directory. Shared by the CLI fallback and the long-running detection server so both resolve
/// the same script and model files.
/// </summary>
public static class SplitDetectionLayout
{
    public const string SubmodulePath = "backend/src/manga-vert-split-nn";
    public const string ScriptName = "detect_breaks.py";
    public const string ServerScriptName = "detect_server.py";
    public const string ModelRelativePath = "models/BCE Only (v8)/final_deployment/best_model.pth";
    public const string ConfigRelativePath =
        "models/BCE Only (v8)/final_deployment/model_config.json";

    public static string ScriptPath =>
        Path.Combine(AppContext.BaseDirectory, SubmodulePath, ScriptName);

    public static string ServerScriptPath =>
        Path.Combine(AppContext.BaseDirectory, SubmodulePath, ServerScriptName);

    public static string CheckpointPath =>
        Path.Combine(AppContext.BaseDirectory, SubmodulePath, ModelRelativePath);

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, SubmodulePath, ConfigRelativePath);
}
