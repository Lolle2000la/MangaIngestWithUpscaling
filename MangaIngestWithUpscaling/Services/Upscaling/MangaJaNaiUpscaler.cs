using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Python;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Reflection;

namespace MangaIngestWithUpscaling.Services.Upscaling;

[RegisterScoped]
public class MangaJaNaiUpscaler(IPythonService pythonService,
    ILogger<MangaJaNaiUpscaler> logger,
    IOptions<UpscalerConfig> sharedConfig) : IUpscaler
{
    private string RunScriptPath => Path.Combine(
        new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName,
        "backend", "src", "run_upscale.py");

    private string ConfigPath => Path.Combine(
        new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName,
        "appstate2.json");

    private string ModelPath => sharedConfig.Value.ModelsDirectory;

    private readonly string[] zipsToDownload =
    [
        "https://github.com/the-database/MangaJaNai/releases/download/1.0.0/IllustrationJaNai_V1_ModelsOnly.zip",
        "https://github.com/the-database/MangaJaNai/releases/download/1.0.0/MangaJaNai_V1_ModelsOnly.zip",
    ];

    private async Task DownloadModelsIfNecessary(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ModelPath))
        {
            Directory.CreateDirectory(ModelPath);
        }
        else
        {
            return;
        }

        var httpClient = new HttpClient();

        // download the zip contents into the models directory. Do not create subdirectories.
        foreach (var zipUrl in zipsToDownload)
        {
            var zipPath = Path.Combine(ModelPath, Path.GetFileName(zipUrl));
            if (!File.Exists(zipPath))
            {
                using var response = await httpClient.GetAsync(zipUrl, cancellationToken);
                response.EnsureSuccessStatusCode();

                // extract the zip file
                ZipFile.ExtractToDirectory(await response.Content.ReadAsStreamAsync(), ModelPath);
            }
        }

    }
    public async Task Upscale(string inputPath, string outputPath, UpscalerProfile profile, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found", inputPath);
        }
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        await DownloadModelsIfNecessary(cancellationToken);

        var outputFilename = Path.GetFileNameWithoutExtension(outputPath);

        if (outputPath.EndsWith(".cbz"))
        {
            
        }

        var config = MangaJaNaiUpscalerConfig.FromUpscalerProfile(profile);
        config.ApplyUpscalerConfig(sharedConfig.Value);
        config.SelectedTabIndex = 0;
        config.InputFilePath = inputPath;
        config.OutputFolderPath = outputDirectory;
        config.OutputFilename = outputFilename;
        config.ModelsDirectory = ModelPath;
        var configPath = JsonWorkflowModifier.ModifyWorkflowConfig(ConfigPath, config);

        logger.LogInformation("Upscaling {inputPath} to {outputPath} with {profile.Name}", inputPath, outputPath, profile.Name);

        string arguments = $"--settings \"{configPath}\"";
        try
        {
            var output = await pythonService.RunPythonScript(RunScriptPath, arguments, cancellationToken);

            logger.LogWarning("Upscaling Output {inputPath}: {line}", inputPath, output);

            logger.LogInformation("Upscaling {inputPath} to {outputPath} with {profile.Name} completed", inputPath, outputPath, profile.Name);
        }
        catch (Exception)
        {
            throw;
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
