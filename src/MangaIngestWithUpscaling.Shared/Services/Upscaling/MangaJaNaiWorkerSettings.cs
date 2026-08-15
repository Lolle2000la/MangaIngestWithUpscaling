using System.Text.Json;
using System.Text.Json.Nodes;
using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Produces the settings file handed to <c>worker.py</c> on spawn. It is derived once from the
/// deployed <c>appstate2.json</c> with the process-wide fields (models directory, FP16, device)
/// patched from runtime configuration. Per-job differences (input, output, format, scale) are
/// passed in the NDJSON job request instead of being baked into a per-job settings file.
/// </summary>
public static class MangaJaNaiWorkerSettings
{
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appstate2.json");

    /// <summary>
    /// Writes the patched settings file and returns its path. Called on each worker spawn so that
    /// configuration changes (e.g. backend/device) are picked up after the idle worker is recycled.
    /// </summary>
    public static string EnsureSettings(UpscalerConfig config)
    {
        // Per-process path so a main app and a remote worker (or two instances) on the same
        // machine don't clobber each other's settings file.
        string path = Path.Combine(
            Path.GetTempPath(),
            $"mangaingest_worker_settings_{Environment.ProcessId}.json"
        );

        string json = File.ReadAllText(ConfigPath);
        JsonNode? root =
            JsonNode.Parse(json) ?? throw new InvalidOperationException("Invalid appstate2.json.");

        if (root is JsonObject obj)
        {
            obj["ModelsDirectory"] = config.ModelsDirectory;
            obj["UseFp16"] = config.UseFp16;
            obj["UseCpu"] = config.UseCPU;
            // In the backend's device indexing scheme, index 0 is CPU.
            obj["SelectedDeviceIndex"] = config.UseCPU ? 0 : config.SelectedDeviceIndex;
        }

        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        );
        return path;
    }
}
