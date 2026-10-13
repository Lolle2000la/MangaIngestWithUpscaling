namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

/// <summary>
/// Filesystem layout of the page-break detector model.
/// </summary>
public static class SplitDetectionLayout
{
    public const string OnnxModelRelativePath = "models/page_break_detector.onnx";

    /// <summary>
    /// Root the bundled detector files resolve under. Defaults to the application base directory; a
    /// test can point it at a temp directory so it does not write into the real install layout.
    /// </summary>
    public static string Root { get; set; } = AppContext.BaseDirectory;

    public static string OnnxModelPath => Path.Combine(Root, OnnxModelRelativePath);

    /// <summary>
    /// Resolves the absolute path to the ONNX page break detector model, searching the configured
    /// models directory first and the application base directory second. Nothing else is probed:
    /// the repository-relative <c>test_data/</c> fallback this used to try was the one place a
    /// production process could load a detector the startup pinned-hash verification never covered
    /// (it only repairs files under the models directory), so a file planted at the process CWD was
    /// run unverified. Tests point <see cref="Root"/> or the models directory at their own fixture
    /// instead.
    /// </summary>
    public static string ResolveModelPath(string? modelsDirectory = null)
    {
        if (!string.IsNullOrEmpty(modelsDirectory))
        {
            string inModels = Path.Combine(modelsDirectory, "page_break_detector.onnx");
            if (File.Exists(inModels))
            {
                return inModels;
            }
        }

        if (File.Exists(OnnxModelPath))
        {
            return OnnxModelPath;
        }

        // Not found anywhere yet: return the path the model is expected at (the configured models
        // directory when one is set, otherwise the base directory) so the caller reports a
        // FileNotFoundException naming where it looked, rather than searching somewhere unverified.
        return !string.IsNullOrEmpty(modelsDirectory)
            ? Path.Combine(modelsDirectory, "page_break_detector.onnx")
            : OnnxModelPath;
    }
}
