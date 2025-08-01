﻿using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

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
            .Where(n => n != null)
            .Select(n => n!.AsObject())
            .FirstOrDefault(obj => obj["WorkflowName"]?.GetValue<string>() == "Upscale Manga (Default)");
        if (workflow == null)
            throw new Exception("Workflow 'Upscale Manga (Default)' not found.");

        // Update only the keys provided in the config.
        if (config.SelectedTabIndex is not null)
            workflow["SelectedTabIndex"] = config.SelectedTabIndex.Value;
        if (!string.IsNullOrEmpty(config.InputFilePath))
            workflow["InputFilePath"] = config.InputFilePath;
        if (!string.IsNullOrEmpty(config.InputFolderPath))
            workflow["InputFolderPath"] = config.InputFolderPath;
        if (!string.IsNullOrEmpty(config.OutputFilename))
            workflow["OutputFilename"] = config.OutputFilename;
        if (!string.IsNullOrEmpty(config.OutputFolderPath))
            workflow["OutputFolderPath"] = config.OutputFolderPath;
        if (config.OverwriteExistingFiles is not null)
            workflow["OverwriteExistingFiles"] = config.OverwriteExistingFiles.Value;
        if (config.UpscaleImages is not null)
            workflow["UpscaleImages"] = config.UpscaleImages.Value;
        if (config.WebpSelected is not null)
            workflow["WebpSelected"] = config.WebpSelected.Value;
        if (config.AvifSelected is not null)
            workflow["AvifSelected"] = config.AvifSelected.Value;
        if (config.PngSelected is not null)
            workflow["PngSelected"] = config.PngSelected.Value;
        if (config.JpegSelected is not null)
            workflow["JpegSelected"] = config.JpegSelected.Value;
        if (config.UpscaleScaleFactor is not null)
            workflow["UpscaleScaleFactor"] = config.UpscaleScaleFactor.Value;
        if (!string.IsNullOrEmpty(config.ModelsDirectory))
            rootNode["ModelsDirectory"] = config.ModelsDirectory;
        if (config.UseFp16 is not null)
            rootNode["UseFp16"] = config.UseFp16;
        if (config.UseCPU is not null)
            rootNode["UseCPU"] = config.UseCPU;
        if (config.SelectedDeviceIndex is not null)
            rootNode["SelectedDeviceIndex"] = config.SelectedDeviceIndex;

        // Write the modified JSON to a temporary file.
        string tempFilePath = Path.GetTempFileName();
        File.WriteAllText(tempFilePath, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // rename to .json
        File.Move(tempFilePath, tempFilePath + ".json");
        return tempFilePath + ".json";
    }
}