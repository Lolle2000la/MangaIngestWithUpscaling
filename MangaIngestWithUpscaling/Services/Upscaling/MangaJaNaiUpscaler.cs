using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Python;
using System.Reflection;

namespace MangaIngestWithUpscaling.Services.Upscaling;

public class MangaJaNaiUpscaler(IPythonService pythonService,
    ILogger<MangaJaNaiUpscaler> logger) : IUpscaler
{
    private string RunScriptPath => Path.Combine(
        new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName, 
        "backend", "src", "run_upscale.py");

    private string ConfigPath => Path.Combine(
        new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName,
        "appstate2.json");

    public async Task Upscale(string inputPath, string outputPath, UpscalerProfile profile, CancellationToken cancellationToken)
    {
        if(!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found", inputPath);
        }
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        var outputFilename = Path.GetFileName(outputPath);

        var config = MangaJaNaiUpscalerConfig.FromUpscalerProfile(profile);
        config.SelectedTabIndex = 0;
        config.InputFilePath = inputPath;
        config.OutputFolderPath = outputDirectory;
        config.OutputFilename = outputFilename;
        var configPath = JsonWorkflowModifier.ModifyWorkflowConfig(ConfigPath, config);

        logger.LogInformation("Upscaling {inputPath} to {outputPath} with {profile.Name}", inputPath, outputPath, profile.Name);

        await foreach (var line in pythonService.RunPythonScript(RunScriptPath, configPath, cancellationToken))
        {
            logger.LogDebug("Upscaling {inputPath}: {line}", inputPath, line);
        }

        logger.LogInformation("Upscaling {inputPath} to {outputPath} with {profile.Name} completed", inputPath, outputPath, profile.Name);
    }
}
