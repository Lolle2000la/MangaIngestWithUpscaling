using System.IO.Compression;
using System.Security.Cryptography;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterScoped]
public class MangaJaNaiUpscaler(
    IPythonService pythonService,
    ILogger<MangaJaNaiUpscaler> logger,
    IOptions<UpscalerConfig> sharedConfig,
    IFileSystem fileSystem,
    IMetadataHandlingService metadataHandling,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    IImageResizeService imageResizeService
) : IUpscaler
{
    private static readonly IReadOnlyDictionary<string, string> expectedModelHashes =
        new Dictionary<string, string>
        {
            {
                "2x_IllustrationJaNai_V1_ESRGAN_120k.pth",
                "5f49a71d3cd0000a51ed0e3adfe5c11824740f1c58f7cb520d8d2d1e924c2b88"
            },
            {
                "4x_IllustrationJaNai_V2standard_DAT2_27k.safetensors",
                "60d94aeada1ce3d767e543abcc5ae5d3eba6a910aba5d72149c2c8c14e30b4ab"
            },
            {
                "2x_MangaJaNai_1200p_V1_ESRGAN_70k.pth",
                "43b784f674bdbf89886a62a64cd5f8d8df92caf4d861bdf4d47dad249ede0267"
            },
            {
                "2x_MangaJaNai_1300p_V1_ESRGAN_75k.pth",
                "15ca3c0f75f97f7bf52065bf7c9b8d602de94ce9e3b078ac58793855eed18589"
            },
            {
                "2x_MangaJaNai_1400p_V1_ESRGAN_70k.pth",
                "a940ad8ebcf6bea5580f2f59df67deb009f054c9b87dbbc58c2e452722f34858"
            },
            {
                "2x_MangaJaNai_1500p_V1_ESRGAN_90k.pth",
                "d91f2d247fa61144c1634a2ba46926acd3956ae90d281a5bed6655f8364a5b2c"
            },
            {
                "2x_MangaJaNai_1600p_V1_ESRGAN_90k.pth",
                "6f5923f812dbc5d6aeed727635a21e74cacddce595afe6135cbd95078f6eee44"
            },
            {
                "2x_MangaJaNai_1920p_V1_ESRGAN_70k.pth",
                "1ad4aa6f64684baa430da1bb472489bff2a02473b14859015884a3852339c005"
            },
            {
                "2x_MangaJaNai_2048p_V1_ESRGAN_95k.pth",
                "146cd009b9589203a8444fe0aa7195709bb5b9fdeaca3808b7fbbd5538f94c41"
            },
            {
                "4x_IllustrationJaNai_V2standard_FDAT_M_52k.safetensors",
                "c1767df9655b279643bd08eac633b90d91d27f86f448b411a16bcdb9718ba6d7"
            },
            {
                "4x_IllustrationJaNai_V2standard_FDAT_XL_18k.safetensors",
                "f7f0d5fd522fca8733c534c305265f670bd08f972cc7050d5ac608d7bfd13d4d"
            },
            {
                "4x_IllustrationJaNai_V1_DAT2_190k.pth",
                "a82f3a2d8d1c676171b86a00048b7a624e3c62c87ec701012f106a171c309fbe"
            },
            {
                "4x_IllustrationJaNai_V1_ESRGAN_135k.pth",
                "c67e76c4b5f0474d5116e5f3885202d1bee68187e1389f82bb90baace24152f8"
            },
            {
                "4x_MangaJaNai_1200p_V1_ESRGAN_70k.pth",
                "6e3a8d21533b731eb3d8eaac1a09cf56290fa08faf8473cbe3debded9ab1ebe1"
            },
            {
                "4x_MangaJaNai_1300p_V1_ESRGAN_75k.pth",
                "eacf8210543446f3573d4ea1625f6fc11a3b2a5e18b38978873944be146417a8"
            },
            {
                "4x_MangaJaNai_1400p_V1_ESRGAN_105k.pth",
                "d77f977a6c6c4bf855dae55f0e9fad6ac2823fa8b2ef883b50e525369fde6a74"
            },
            {
                "4x_MangaJaNai_1500p_V1_ESRGAN_105k.pth",
                "5e5174b60316e9abb7875e6d2db208fec4ffc34f3d09fa7f0e0f6476f9d31687"
            },
            {
                "4x_MangaJaNai_1600p_V1_ESRGAN_70k.pth",
                "c126ec8d4b7434d8f6a43d24bec1f56d343104ab8a86b5e01d5d25be6b5244c0"
            },
            {
                "4x_MangaJaNai_1920p_V1_ESRGAN_105k.pth",
                "d469e96e590a25a86037760b26d51405c77759a55b0966b15dc76b609f72f20b"
            },
            {
                "4x_MangaJaNai_2048p_V1_ESRGAN_70k.pth",
                "f70e08c60da372b7207e7348486ea6b498ea8dea6246bb717530a4d45c955b9b"
            },
        };

    private readonly (string, string)[] zipsToDownload =
    [
        (
            "https://github.com/the-database/MangaJaNai/releases/download/1.0.0/IllustrationJaNai_V1_ModelsOnly.zip",
            "6f5496f5ded597474290403de73d7a46c3f8ed328261db2e6ff830a415a6f60b"
        ),
        (
            "https://github.com/the-database/MangaJaNai/releases/download/2.0.0/4x_IllustrationJaNai_V2standard_ModelsOnly.zip",
            "deb0e71aa63257692399419e33991be0496c037049948e4207936b4145d20ba5"
        ),
        (
            "https://github.com/the-database/MangaJaNai/releases/download/1.0.0/MangaJaNai_V1_ModelsOnly.zip",
            "5156f4167875bba51a8ed52bd1c794b0d7277f7103f99b397518066e4dda7e55"
        ),
    ];

    private static string RunScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "backend", "src", "run_upscale.py");

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appstate2.json");

    private string ModelPath => sharedConfig.Value.ModelsDirectory;

    public async Task DownloadModelsIfNecessary(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ModelPath))
        {
            fileSystem.CreateDirectory(ModelPath);
        }

        // Check if all required models exist with correct hashes
        bool needsDownload = await ShouldDownloadModels(cancellationToken);

        if (needsDownload)
        {
            await DownloadAndExtractModels(cancellationToken);
        }

        // Verify all model file hashes after download/extraction
        await VerifyModelHashes(cancellationToken);
    }

    public async Task Upscale(
        string inputPath,
        string outputPath,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    )
    {
        // Delegate to the overload without emitting progress
        await Upscale(inputPath, outputPath, profile, progress: null!, cancellationToken);
    }

    public async Task Upscale(
        string inputPath,
        string outputPath,
        UpscalerProfile profile,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    )
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

        string outputFilename = Path.GetFileNameWithoutExtension(outputPath);

        if (!outputPath.EndsWith(".cbz"))
        {
            throw new ArgumentException("Output path must be a cbz file", nameof(outputPath));
        }

        if (File.Exists(outputPath))
        {
            if (await metadataHandling.PagesEqualAsync(inputPath, outputPath))
            {
                logger.LogInformation(
                    "The target to upscale is seemingly already upscaled, so we will accept this as is.\n\n"
                        + "Tried to upscale \"{inputPath}\" with the target location {outputPath}.",
                    inputPath,
                    outputPath
                );
                return;
            }

            File.Delete(outputPath);
        }

        string actualInputPath = inputPath;

        // Check if we need to preprocess images before upscaling (resize or format conversion)
        bool needsPreprocessing =
            (
                sharedConfig.Value.MaxDimensionBeforeUpscaling.HasValue
                && sharedConfig.Value.MaxDimensionBeforeUpscaling.Value > 0
            )
            || (
                sharedConfig.Value.ImageFormatConversionRules != null
                && sharedConfig.Value.ImageFormatConversionRules.Count > 0
            );

        if (needsPreprocessing)
        {
            var preprocessingOptions = new ImagePreprocessingOptions
            {
                MaxDimension = sharedConfig.Value.MaxDimensionBeforeUpscaling,
                FormatConversionRules =
                    sharedConfig.Value.ImageFormatConversionRules
                    ?? new List<ImageFormatConversionRule>(),
            };

            logger.LogInformation(
                "Creating temporary preprocessed CBZ (max dimension: {MaxDimension}, conversion rules: {RuleCount}) for {InputPath}",
                preprocessingOptions.MaxDimension?.ToString() ?? "none",
                preprocessingOptions.FormatConversionRules.Count,
                inputPath
            );

            using var tempPreprocessedCbz = await imageResizeService.CreatePreprocessedTempCbzAsync(
                inputPath,
                preprocessingOptions,
                cancellationToken
            );

            actualInputPath = tempPreprocessedCbz.FilePath;

            logger.LogInformation(
                "Using preprocessed temporary file for upscaling: {TempPath}",
                actualInputPath
            );

            await PerformUpscaling(
                actualInputPath,
                outputPath,
                outputDirectory,
                outputFilename,
                profile,
                progress,
                cancellationToken
            );
        }
        else
        {
            await PerformUpscaling(
                actualInputPath,
                outputPath,
                outputDirectory,
                outputFilename,
                profile,
                progress,
                cancellationToken
            );
        }
    }

    private async Task PerformUpscaling(
        string inputPath,
        string outputPath,
        string outputDirectory,
        string outputFilename,
        UpscalerProfile profile,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        MangaJaNaiUpscalerConfig config = MangaJaNaiUpscalerConfig.FromUpscalerProfile(profile);
        config.ApplyUpscalerConfig(sharedConfig.Value);
        config.SelectedTabIndex = 0;
        config.InputFilePath = inputPath;
        config.OutputFolderPath = outputDirectory;
        config.OutputFilename = outputFilename;
        config.ModelsDirectory = ModelPath;
        string configPath = JsonWorkflowModifier.ModifyWorkflowConfig(ConfigPath, config);

        logger.LogInformation(
            "Upscaling {inputPath} to {outputPath} with {profile.Name}",
            inputPath,
            outputPath,
            profile.Name
        );

        string arguments = $"--settings \"{configPath}\"";
        try
        {
            // If caller provided a progress reporter, use streaming mode; otherwise run non-streaming
            if (progress is null)
            {
                string output = await pythonService.RunPythonScript(
                    RunScriptPath,
                    arguments,
                    cancellationToken,
                    sharedConfig.Value.UpscaleTimeout
                );
                fileSystem.ApplyPermissions(outputPath);
                logger.LogDebug("Upscaling Output {inputPath}: {output}", inputPath, output);
            }
            else
            {
                int? total = null;
                int current = 0;
                string? lastPhase = null;

                await pythonService.RunPythonScriptStreaming(
                    RunScriptPath,
                    arguments,
                    line =>
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(line))
                                return Task.CompletedTask;

                            // Normalize line
                            var l = line.Trim();
                            if (l.StartsWith("TOTALZIP=", StringComparison.OrdinalIgnoreCase))
                            {
                                var value = l.Substring("TOTALZIP=".Length);
                                if (int.TryParse(value, out var t))
                                {
                                    total = t;
                                    progress.Report(
                                        new UpscaleProgress(
                                            total,
                                            current,
                                            lastPhase,
                                            "Reading archive"
                                        )
                                    );
                                }

                                return Task.CompletedTask;
                            }

                            if (l.StartsWith("PROGRESS=", StringComparison.OrdinalIgnoreCase))
                            {
                                var phase = l.Substring("PROGRESS=".Length);
                                lastPhase = phase;

                                // Heuristics: increment when we see per-image progress events
                                if (
                                    phase.Contains(
                                        "postprocess_worker_zip_image",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    || phase.Contains(
                                        "postprocess_worker_image",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    || phase.Contains(
                                        "postprocess_worker_folder",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    current++;
                                }

                                progress.Report(
                                    new UpscaleProgress(total, current, lastPhase, null)
                                );
                                return Task.CompletedTask;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Ignoring progress parse error: {Line}", line);
                        }

                        return Task.CompletedTask;
                    },
                    cancellationToken,
                    sharedConfig.Value.UpscaleTimeout
                );

                fileSystem.ApplyPermissions(outputPath);
            }

            await upscalerJsonHandlingService.WriteUpscalerJsonAsync(
                outputPath,
                profile,
                cancellationToken
            );

            logger.LogInformation(
                "Upscaling {inputPath} to {outputPath} with {profile.Name} completed",
                inputPath,
                outputPath,
                profile.Name
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Upscaling {inputPath} to {outputPath} with {profile.Name} failed",
                inputPath,
                outputPath,
                profile.Name
            );
            throw;
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private async Task<bool> ShouldDownloadModels(CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();

        foreach (var (fileName, expectedHash) in expectedModelHashes)
        {
            string filePath = Path.Combine(ModelPath, fileName);
            if (!File.Exists(filePath))
            {
                logger.LogInformation(
                    "Model file {fileName} not found, download required",
                    fileName
                );
                return true;
            }

            await using FileStream stream = File.OpenRead(filePath);
            byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            string hashString = Convert.ToHexStringLower(hash);
            if (hashString != expectedHash)
            {
                logger.LogWarning(
                    "Model file {fileName} has incorrect hash, download required. Expected: {expectedHash}, Actual: {hashString}",
                    fileName,
                    expectedHash,
                    hashString
                );
                return true;
            }
        }

        logger.LogInformation("All model files are present with correct hashes");
        return false;
    }

    private async Task DownloadAndExtractModels(CancellationToken cancellationToken)
    {
        var httpClient = new HttpClient();
        using var sha256 = SHA256.Create();

        // Clear the models directory before downloading to ensure clean state
        if (Directory.Exists(ModelPath))
        {
            logger.LogInformation("Clearing existing models directory for fresh download");
            Directory.Delete(ModelPath, true);
        }

        fileSystem.CreateDirectory(ModelPath);

        // download the zip contents into the models directory. Do not create subdirectories.
        foreach (var (zipUrl, sha256Hash) in zipsToDownload)
        {
            logger.LogInformation("Downloading {zipUrl}", zipUrl);
            using HttpResponseMessage response = await httpClient.GetAsync(
                zipUrl,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();

            byte[] zipContent = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // verify the zip hash
            byte[] hash = sha256.ComputeHash(zipContent);
            string hashString = Convert.ToHexStringLower(hash);
            if (hashString != sha256Hash)
            {
                throw new Exception(
                    $"Hash mismatch for {zipUrl}. Expected: {sha256Hash}, Actual: {hashString}"
                );
            }

            // extract the zip file
            using var zipStream = new MemoryStream(zipContent);
            ZipFile.ExtractToDirectory(zipStream, ModelPath);

            logger.LogInformation("Successfully downloaded and extracted {zipUrl}", zipUrl);
        }
    }

    private async Task VerifyModelHashes(CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();

        foreach (var (fileName, expectedHash) in expectedModelHashes)
        {
            string filePath = Path.Combine(ModelPath, fileName);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"Model file not found after download: {fileName}",
                    filePath
                );
            }

            await using FileStream stream = File.OpenRead(filePath);
            byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            string hashString = Convert.ToHexStringLower(hash);
            if (hashString != expectedHash)
            {
                throw new Exception(
                    $"Hash verification failed for model file {fileName}. Expected: {expectedHash}, Actual: {hashString}"
                );
            }
        }

        logger.LogInformation("All model files verified successfully");
    }
}
