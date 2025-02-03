using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Provides functionality to modify a JSON workflow configuration using .NET 9 JSON facilities.
/// </summary>
public static class JsonWorkflowModifier
{
    /// <summary>
    /// Updates the JSON document based on the provided config.
    /// Only keys with non-null values in the config are modified.
    /// Writes the full modified JSON to a temporary file and returns its path.
    /// </summary>
    /// <param name="originalFile">The JSON document that will be modified.</param>
    /// <param name="config">Configuration object with keys to update.</param>
    /// <returns>Path to the temporary file containing the modified JSON.</returns>
    /// <exception cref="Exception">Thrown if the expected JSON structure is not found.</exception>
    public static string ModifyWorkflowConfig(string originalFile, MangaJaNaiUpscalerConfig config)
    {
        // Read the JSON document from the original file.
        string jsonContent = File.ReadAllText(originalFile);
        // Parse the JSON document into a mutable DOM.
        JsonNode? rootNode = JsonNode.Parse(jsonContent);
        if (rootNode == null)
            throw new Exception("Invalid JSON document.");

        // Navigate to Workflows -> $values.
        JsonObject? workflowsObj = rootNode["Workflows"]?.AsObject();
        if (workflowsObj == null)
            throw new Exception("Invalid JSON format: 'Workflows' not found.");
        JsonArray? valuesArray = workflowsObj["$values"]?.AsArray();
        if (valuesArray == null)
            throw new Exception("Invalid JSON format: 'Workflows.$values' not found.");

        // Find the workflow with WorkflowName == "Upscale Manga (Default)".
        JsonObject? workflow = valuesArray
            .Select(n => n.AsObject())
            .FirstOrDefault(obj => obj["WorkflowName"]?.GetValue<string>() == "Upscale Manga (Default)");
        if (workflow == null)
            throw new Exception("Workflow 'Upscale Manga (Default)' not found.");

        // Update only the keys provided in the config.
        if (config.SelectedTabIndex.HasValue)
            workflow["SelectedTabIndex"] = config.SelectedTabIndex.Value;
        if (!string.IsNullOrEmpty(config.InputFilePath))
            workflow["InputFilePath"] = config.InputFilePath;
        if (!string.IsNullOrEmpty(config.InputFolderPath))
            workflow["InputFolderPath"] = config.InputFolderPath;
        if (!string.IsNullOrEmpty(config.OutputFilename))
            workflow["OutputFilename"] = config.OutputFilename;
        if (!string.IsNullOrEmpty(config.OutputFolderPath))
            workflow["OutputFolderPath"] = config.OutputFolderPath;
        if (config.OverwriteExistingFiles.HasValue)
            workflow["OverwriteExistingFiles"] = config.OverwriteExistingFiles.Value;
        if (config.UpscaleImages.HasValue)
            workflow["UpscaleImages"] = config.UpscaleImages.Value;
        if (config.WebpSelected.HasValue)
            workflow["WebpSelected"] = config.WebpSelected.Value;
        if (config.AvifSelected.HasValue)
            workflow["AvifSelected"] = config.AvifSelected.Value;
        if (config.PngSelected.HasValue)
            workflow["PngSelected"] = config.PngSelected.Value;
        if (config.JpegSelected.HasValue)
            workflow["JpegSelected"] = config.JpegSelected.Value;
        if (config.UpscaleScaleFactor.HasValue)
            workflow["UpscaleScaleFactor"] = config.UpscaleScaleFactor.Value;

        // Write the modified JSON to a temporary file.
        string tempFilePath = Path.GetTempFileName();
        File.WriteAllText(tempFilePath, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return tempFilePath;
    }
}
