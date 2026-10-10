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
    /// models directory, application base, and repository relative locations.
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

        // Only search host/repository fallback locations when running under the default root.
        if (Root == AppContext.BaseDirectory)
        {
            string directModelsPath = Path.Combine(
                AppContext.BaseDirectory,
                "models",
                "page_break_detector.onnx"
            );
            if (File.Exists(directModelsPath))
            {
                return directModelsPath;
            }

            string currentTestData = Path.Combine(
                Directory.GetCurrentDirectory(),
                "test_data",
                "models",
                "page_break_detector.onnx"
            );
            if (File.Exists(currentTestData))
            {
                return currentTestData;
            }

            string upwardTestData = Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "test_data",
                "models",
                "page_break_detector.onnx"
            );
            if (File.Exists(upwardTestData))
            {
                return Path.GetFullPath(upwardTestData);
            }
        }

        return !string.IsNullOrEmpty(modelsDirectory)
            ? Path.Combine(modelsDirectory, "page_break_detector.onnx")
            : OnnxModelPath;
    }
}
