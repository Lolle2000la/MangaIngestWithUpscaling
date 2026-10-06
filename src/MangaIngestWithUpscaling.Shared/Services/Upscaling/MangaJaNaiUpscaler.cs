using System.IO.Compression;
using System.Security.Cryptography;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterScoped]
public class MangaJaNaiUpscaler(
    IMangaJaNaiWorkerClient workerClient,
    ILogger<MangaJaNaiUpscaler> logger,
    IOptions<UpscalerConfig> sharedConfig,
    IFileSystem fileSystem,
    IMetadataHandlingService metadataHandling,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    IImageResizeService imageResizeService,
    IStringLocalizer<MangaJaNaiUpscaler> localizer
) : IUpscaler
{
    private record ModelPackage(
        string ZipUrl,
        string ZipHash,
        Dictionary<string, string> ExpectedFileHashes
    );

    private readonly List<ModelPackage> modelPackages =
    [
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/MangaJaNai_V1_ONNX.zip",
            "815a7160ae301ee64dd74ae78bf61575d4a64b5a1f27fa3157738f4d25d78955",
            new Dictionary<string, string>
            {
                {
                    "2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "246871e7aa2a9b0365316e7f3691eb4e27cde0c90a0780cd7d8c53698bad6adc"
                },
                {
                    "2x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "ca38dce56dd311fea9c07f1772d9df75ceeddf2ef8268bdb34842fc9c6e4c9ba"
                },
                {
                    "2x_MangaJaNai_1400p_V1_ESRGAN_70k.onnx",
                    "cc932c54311a8b19af0f0a6b948f76582452bad1323e76a0f6c31f0088f79eac"
                },
                {
                    "2x_MangaJaNai_1500p_V1_ESRGAN_90k.onnx",
                    "68d44b89a6f861a9477bc93ad3e60f1eabdd3a78c1242662ddea53d6f73972e4"
                },
                {
                    "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
                    "95ff2d3bed1c2675b04b2c0f716080dcfbb0597214e3a524b396cc14c2302930"
                },
                {
                    "2x_MangaJaNai_1920p_V1_ESRGAN_70k.onnx",
                    "d85d7cf7ba847f64dbf54a767660d6db9f8fdec7953ee7ea75c21602635f7b82"
                },
                {
                    "2x_MangaJaNai_2048p_V1_ESRGAN_95k.onnx",
                    "41aa377ad78416574d526c99e906b4fce15c07dd293c41147f9e0841d7eb4c03"
                },
                {
                    "4x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "5d6eaabe6ff7d9f254b7d9fdc7f8002577ffe6aa4125c507ecc2567e89a9d2c2"
                },
                {
                    "4x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "f94f955917d958983916df1ba7c01ff7a3ac5d11e0fccb4d04cb47672e5abab7"
                },
                {
                    "4x_MangaJaNai_1400p_V1_ESRGAN_105k.onnx",
                    "c183fac0e35f559b31a9730b995b32c3945e5e7b44cde0f5a7ff8d8a6f82a549"
                },
                {
                    "4x_MangaJaNai_1500p_V1_ESRGAN_105k.onnx",
                    "11725587263a8fc24b387f734751836c6c8dcc85936103a0d163ec6bfa6269bf"
                },
                {
                    "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                    "7fe4c92b5e2ce1e47af4950e4ed816d9f425f67d1448a9b9ec6d0cfef0dfbadf"
                },
                {
                    "4x_MangaJaNai_1920p_V1_ESRGAN_105k.onnx",
                    "4065b3a64124f803992bab36a048e149278de8eb551357ccd0d5c15160bf5e42"
                },
                {
                    "4x_MangaJaNai_2048p_V1_ESRGAN_70k.onnx",
                    "29c4151541b885e7ab7647b3b6eeff46a8f645c4f8ffe9bec8b2bd9a4b83e937"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V1_ONNX.zip",
            "58e889e0dc7d44e5a581a9bac1c7f25e50f9566b9b483dc24ac1dd87b364571a",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V1_ESRGAN_120k.onnx",
                    "a784f6b50718d7e836d3130050c1e63fddf40e509b5b46531b3fe24de3842cc5"
                },
                {
                    "4x_IllustrationJaNai_V1_DAT2_190k.onnx",
                    "3b96724115d3e01deb4cdb2cee28ed7f14084a95db9ddd825bd3da96fcd4945a"
                },
                {
                    "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
                    "ff598bdc56b1e9f713dfb673385995f6de5acfde5c83da718b65863c85eea472"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/4x_IllustrationJaNai_V2standard_ONNX.zip",
            "250131d9dec58b03fe8048ddddb9bd2bf004fef8a23328980a4767a2ca1eca0a",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V2standard_FDAT_M_unshuffle_40k.onnx",
                    "36ca1bca237d7cea38d963f15acf34c770d6ed6694e47f5865c8c3e5c8adc52f"
                },
                {
                    "4x_IllustrationJaNai_V2standard_DAT2_27k.onnx",
                    "a23a4515489ecffc7b07db48f0fd9293b47420cee9bfeab1064ce5564b3aae6f"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
                    "ebe1824cd156e0468a859d4ef6a45a98999a588e761a232610cdfbb0182f604a"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_XL_18k.onnx",
                    "d5b52c56f1efcc908b54217d7a2c947a310e870a6f4fd1277f4af67cd691b876"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3denoise_ONNX.zip",
            "3f1f41afd3427edefd7a0a71544dc411a0d1128dff9afa61f7d8c204f3fb0f2f",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16.onnx",
                    "9c65af1efcabda1dc7b692bbed0152d8223141f77db91d46d8405298ff145cd1"
                },
                {
                    "2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16.onnx",
                    "d29ac4cef6adf66ca72fe1757d20f57798351f1d4b737c837ad35a9fd30a8633"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_DAT2_27k_bf16.onnx",
                    "252bc376f5af1d1fd266fb799d082fe31559139453c2fdeb6bca0e593afd5f07"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_M_47k_fp16.onnx",
                    "54d01f4abd3b96a2f29a0dc88ac7370723b2ea568ec230767eaf40db5c18610f"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_XL_32k_bf16.onnx",
                    "24ab63e4c71f0daaa0796d61cc59b631a60b8b16869543103bb0515a9a83a418"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3detail_ONNX.zip",
            "4b1dfe45d44a7c758eb870ec0d223e54e36d2fcc573c566bc33bb80ea645ee30",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3detail_FDAT_M_unshuffle_40k_fp16.onnx",
                    "f4e6a4aefbff8576b7e3b187366a572c5226cac78cc3cfb52ec1cdc0f9b84dd3"
                },
                {
                    "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
                    "040f3d24bcb26cead6548e39972a9165775174d1b297ee404aefbda1a8380b5d"
                },
                {
                    "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
                    "e45739ffbe2c8be3d2c3d7e94863358263aaf2b2f3d77f11e1b40d85daf7f053"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
                    "89060f808b2235244f22429a68271a415f6d3ae6450fd6dc1a04fed4da5c8b2b"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx",
                    "0a444065604ed6cde6591c7cd307d32963f92791cd303922561d5aaf6f53c915"
                },
                {
                    "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
                    "f22ce9f13a2c013b1d18837471dfc8ff1f51574c0f2100a024dee67a302dd565"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/manga-vert-split-nn/releases/download/1.0.0/page_break_detector.onnx.zip",
            "709e63a144105bc618b1558a9dbc9f04e9a970ccd1a6f34915eb0af1c0e91d22",
            new Dictionary<string, string>
            {
                {
                    "page_break_detector.onnx",
                    "1ef8a11980137aabef118d18f8ea417b1f7e7fd9277724015cff01688a412826"
                },
                {
                    "page_break_detector.onnx.data",
                    "a27c08523b1683cb942206c3eb04539a4fdc8d1e023fb708fe7350992e3de419"
                },
            }
        ),
    ];

    private string ModelPath => sharedConfig.Value.ResolvedModelsDirectory;

    public async Task DownloadModelsIfNecessary(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ModelPath))
        {
            fileSystem.CreateDirectory(ModelPath);
        }

        foreach (var package in modelPackages)
        {
            bool needsDownload = await ShouldDownloadPackage(package, cancellationToken);

            if (needsDownload)
            {
                await DownloadAndExtractPackage(package, cancellationToken);
            }

            // Verify all model file hashes after download/extraction
            await VerifyPackageHashes(package, cancellationToken);
        }
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
        using IPreprocessedInput preprocessed = await PreprocessAsync(
            inputPath,
            profile,
            cancellationToken
        );
        await UpscalePreprocessedAsync(
            preprocessed,
            outputPath,
            profile,
            progress,
            cancellationToken
        );
    }

    public async Task<IPreprocessedInput> PreprocessAsync(
        string inputPath,
        UpscalerProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException(localizer["Error_InputFileNotFound"], inputPath);
        }

        if (!ImagePreprocessingOptions.IsEnabled(sharedConfig.Value))
        {
            return new PassThroughPreprocessedInput(inputPath);
        }

        ImagePreprocessingOptions preprocessingOptions = ImagePreprocessingOptions.FromConfig(
            sharedConfig.Value
        );

        logger.LogInformation(
            "Creating temporary preprocessed CBZ (max dimension: {MaxDimension}, conversion rules: {RuleCount}, smart downscale: {SmartDownscale}) for {InputPath}",
            preprocessingOptions.MaxDimension?.ToString() ?? "none",
            preprocessingOptions.FormatConversionRules.Count,
            preprocessingOptions.EnableSmartDownscale,
            inputPath
        );

        TempResizedCbz temp = await imageResizeService.CreatePreprocessedTempCbzAsync(
            inputPath,
            preprocessingOptions,
            cancellationToken
        );

        logger.LogInformation(
            "Using preprocessed temporary file for upscaling: {TempPath}",
            temp.FilePath
        );

        return new TempPreprocessedInput(temp);
    }

    public async Task UpscalePreprocessedAsync(
        IPreprocessedInput preprocessed,
        string outputPath,
        UpscalerProfile profile,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        string inputPath = preprocessed.InputPath;

        string outputDirectory = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(outputDirectory))
        {
            fileSystem.CreateDirectory(outputDirectory);
        }

        string outputFilename = Path.GetFileNameWithoutExtension(outputPath);

        if (!outputPath.EndsWith(".cbz"))
        {
            throw new ArgumentException(localizer["Error_OutputPathMustBeCbz"], nameof(outputPath));
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

        await PerformUpscaling(
            inputPath,
            outputPath,
            outputDirectory,
            outputFilename,
            profile,
            progress,
            cancellationToken
        );
    }

    private sealed class PassThroughPreprocessedInput(string inputPath) : IPreprocessedInput
    {
        public string InputPath { get; } = inputPath;

        public void Dispose() { }
    }

    private sealed class TempPreprocessedInput(TempResizedCbz temp) : IPreprocessedInput
    {
        public string InputPath => temp.FilePath;

        public void Dispose() => temp.Dispose();
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
        logger.LogInformation(
            "Upscaling {inputPath} to {outputPath} with {profile.Name}",
            inputPath,
            outputPath,
            profile.Name
        );

        var request = new UpscaleJobRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            InputPath = inputPath,
            OutputFolder = outputDirectory,
            OutputFilename = outputFilename,
            Format = profile.CompressionFormat,
            Scale = profile.ScalingFactor,
            Quality = profile.Quality,
            Overwrite = true,
        };

        try
        {
            TimeSpan scaledTimeout = await ComputeScaledTimeoutAsync(inputPath, cancellationToken);

            await workerClient.RunJobAsync(request, progress, cancellationToken, scaledTimeout);

            fileSystem.ApplyPermissions(outputPath);

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
    }

    /// <summary>
    /// Calculates the upscale timeout scaled by the size of the largest image in the CBZ.
    /// Used by both streaming and non-streaming execution paths.
    /// The base <see cref="UpscalerConfig.UpscaleTimeout"/> is treated as the per-million-pixel
    /// allowance, so larger images automatically receive proportionally more time.
    /// The timeout is never reduced below the base value.
    /// </summary>
    private async Task<TimeSpan> ComputeScaledTimeoutAsync(
        string cbzPath,
        CancellationToken cancellationToken
    )
    {
        long maxPixels = await imageResizeService.GetMaxPixelCountFromCbzAsync(
            cbzPath,
            cancellationToken
        );

        if (maxPixels <= 0)
            return sharedConfig.Value.UpscaleTimeout;

        double scalingFactor = Math.Max(1.0, maxPixels / 1_000_000.0);
        TimeSpan scaledTimeout = sharedConfig.Value.UpscaleTimeout * scalingFactor;

        logger.LogDebug(
            "Scaled upscale timeout for {CbzPath}: {BaseTimeout} × {ScalingFactor:F2} = {ScaledTimeout} (max image: {MaxPixels} px)",
            cbzPath,
            sharedConfig.Value.UpscaleTimeout,
            scalingFactor,
            scaledTimeout,
            maxPixels
        );

        return scaledTimeout;
    }

    private async Task<bool> ShouldDownloadPackage(
        ModelPackage package,
        CancellationToken cancellationToken
    )
    {
        using var sha256 = SHA256.Create();

        foreach (var (fileName, expectedHash) in package.ExpectedFileHashes)
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

        return false;
    }

    private async Task DownloadAndExtractPackage(
        ModelPackage package,
        CancellationToken cancellationToken
    )
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        using var sha256 = SHA256.Create();

        logger.LogInformation("Downloading {zipUrl}", package.ZipUrl);
        using HttpResponseMessage response = await httpClient.GetAsync(
            package.ZipUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        string tempZip = Path.Combine(Path.GetTempPath(), $"model_pkg_{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fileStream = File.Create(tempZip))
            await using (
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            )
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }

            // verify the zip hash
            await using (var fileStream = File.OpenRead(tempZip))
            {
                byte[] hash = await sha256.ComputeHashAsync(fileStream, cancellationToken);
                string hashString = Convert.ToHexStringLower(hash);
                if (hashString != package.ZipHash)
                {
                    throw new Exception(
                        localizer[
                            "Error_ZipHashMismatch",
                            package.ZipUrl,
                            package.ZipHash,
                            hashString
                        ]
                    );
                }
            }

            // extract the zip file
            ZipFile.ExtractToDirectory(tempZip, ModelPath, true);
            logger.LogInformation("Successfully downloaded and extracted {zipUrl}", package.ZipUrl);
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch { }
        }
    }

    private async Task VerifyPackageHashes(
        ModelPackage package,
        CancellationToken cancellationToken
    )
    {
        using var sha256 = SHA256.Create();

        foreach (var (fileName, expectedHash) in package.ExpectedFileHashes)
        {
            string filePath = Path.Combine(ModelPath, fileName);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    localizer["Error_ModelFileNotFound", fileName],
                    filePath
                );
            }

            await using FileStream stream = File.OpenRead(filePath);
            byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            string hashString = Convert.ToHexStringLower(hash);
            if (hashString != expectedHash)
            {
                throw new Exception(
                    localizer["Error_ModelHashMismatch", fileName, expectedHash, hashString]
                );
            }
        }

        logger.LogInformation(
            "All model files verified successfully for package {ZipUrl}",
            package.ZipUrl
        );
    }
}
