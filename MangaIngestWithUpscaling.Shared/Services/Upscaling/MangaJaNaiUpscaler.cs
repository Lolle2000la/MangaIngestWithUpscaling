using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterScoped]
public class MangaJaNaiUpscaler(
    IPythonService pythonService,
    ILogger<MangaJaNaiUpscaler> logger,
    IOptions<UpscalerConfig> sharedConfig,
    IFileSystem fileSystem,
    IMetadataHandlingService metadataHandling) : IUpscaler
{
    private readonly (string, string)[] zipsToDownload =
    [
        ("https://github.com/the-database/MangaJaNai/releases/download/1.0.0/IllustrationJaNai_V1_ModelsOnly.zip",
            "6f5496f5ded597474290403de73d7a46c3f8ed328261db2e6ff830a415a6f60b"),
        ("https://github.com/the-database/MangaJaNai/releases/download/1.0.0/MangaJaNai_V1_ModelsOnly.zip",
            "5156f4167875bba51a8ed52bd1c794b0d7277f7103f99b397518066e4dda7e55")
    ];

    private static string RunScriptPath => Path.Combine(
        AppContext.BaseDirectory,
        "backend", "src", "run_upscale.py");

    private static string ConfigPath => Path.Combine(
        AppContext.BaseDirectory,
        "appstate2.json");

    private string ModelPath => sharedConfig.Value.ModelsDirectory;

    public async Task DownloadModelsIfNecessary(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ModelPath))
        {
            fileSystem.CreateDirectory(ModelPath);
        }
        else
        {
            return;
        }

        var httpClient = new HttpClient();

        using var sha256 = SHA256.Create();

        // download the zip contents into the models directory. Do not create subdirectories.
        foreach (var (zipUrl, sha256Hash) in zipsToDownload)
        {
            string zipPath = Path.Combine(ModelPath, Path.GetFileName(zipUrl));
            if (!File.Exists(zipPath))
            {
                using var response = await httpClient.GetAsync(zipUrl, cancellationToken);
                response.EnsureSuccessStatusCode();

                // verify the hash
                byte[] hash = await sha256.ComputeHashAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken);
                string hashString = Convert.ToHexStringLower(hash);
                if (hashString != sha256Hash)
                {
                    throw new Exception($"Hash mismatch for {zipUrl}");
                }

                // extract the zip file
                ZipFile.ExtractToDirectory(await response.Content.ReadAsStreamAsync(cancellationToken), ModelPath);
            }
        }
    }

    public async Task Upscale(string inputPath, string outputPath, UpscalerProfile profile,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found", inputPath);
        }

        string outputDirectory = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(outputDirectory))
        {
            fileSystem.CreateDirectory(outputDirectory);
        }

        await DownloadModelsIfNecessary(cancellationToken);

        string outputFilename = Path.GetFileNameWithoutExtension(outputPath);

        if (!outputPath.EndsWith(".cbz"))
        {
            throw new ArgumentException("Output path must be a cbz file", nameof(outputPath));
        }

        if (File.Exists(outputPath))
        {
            if (metadataHandling.PagesEqual(inputPath, outputPath))
            {
                logger.LogInformation(
                    "The target to upscale is seemingly already upscaled, so we will accept this as is.\n\n" +
                    "Tried to upscale \"{inputPath}\" with the target location {outputPath}.", inputPath, outputPath);
                return;
            }
            else
            {
                File.Delete(outputPath);
            }
        }

        var config = MangaJaNaiUpscalerConfig.FromUpscalerProfile(profile);
        config.ApplyUpscalerConfig(sharedConfig.Value);
        config.SelectedTabIndex = 0;
        config.InputFilePath = inputPath;
        config.OutputFolderPath = outputDirectory;
        config.OutputFilename = outputFilename;
        config.ModelsDirectory = ModelPath;
        string configPath = JsonWorkflowModifier.ModifyWorkflowConfig(ConfigPath, config);

        logger.LogInformation("Upscaling {inputPath} to {outputPath} with {profile.Name}", inputPath, outputPath,
            profile.Name);

        string arguments = $"--settings \"{configPath}\"";
        try
        {
            string output = await pythonService.RunPythonScript(RunScriptPath, arguments, cancellationToken,
                sharedConfig.Value.UpscaleTimeout);
            fileSystem.ApplyPermissions(outputPath);

            logger.LogDebug("Upscaling Output {inputPath}: {output}", inputPath, output);

            var upscalerJson = new UpscalerProfileJsonDto
            {
                Name = profile.Name,
                UpscalerMethod = profile.UpscalerMethod,
                ScalingFactor = (int)profile.ScalingFactor,
                CompressionFormat = profile.CompressionFormat,
                Quality = profile.Quality
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(upscalerJson, options);

            using (ZipArchive archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry? existingEntry = archive.GetEntry("upscaler.json");
                existingEntry?.Delete();

                ZipArchiveEntry entry = archive.CreateEntry("upscaler.json");
                await using (Stream stream = entry.Open())
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync(jsonString);
                }
            }

            logger.LogInformation("Upscaling {inputPath} to {outputPath} with {profile.Name} completed", inputPath,
                outputPath, profile.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upscaling {inputPath} to {outputPath} with {profile.Name} failed", inputPath,
                outputPath, profile.Name);
            throw;
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}