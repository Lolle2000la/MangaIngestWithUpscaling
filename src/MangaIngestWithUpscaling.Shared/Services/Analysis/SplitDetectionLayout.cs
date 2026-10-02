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

    /// <summary>
    /// Root the bundled detector files resolve under. Defaults to the application base directory; a
    /// test can point it at a temp directory so it does not write into the real install layout.
    /// </summary>
    public static string Root { get; set; } = AppContext.BaseDirectory;

    public static string ScriptPath => Path.Combine(Root, SubmodulePath, ScriptName);

    public static string ServerScriptPath => Path.Combine(Root, SubmodulePath, ServerScriptName);

    public static string CheckpointPath => Path.Combine(Root, SubmodulePath, ModelRelativePath);

    public static string ConfigPath => Path.Combine(Root, SubmodulePath, ConfigRelativePath);
}
