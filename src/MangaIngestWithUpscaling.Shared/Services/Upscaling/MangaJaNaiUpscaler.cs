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

    private static readonly ModelPackage PageBreakDetectorPackage = new(
        "https://github.com/Lolle2000la/manga-vert-split-nn/releases/download/1.0.0/page_break_detector.onnx.zip",
        "122c184da5b4de58f68e91d25cfb07d684ea58b016727b8182af2193addc2e77",
        new Dictionary<string, string>
        {
            {
                "page_break_detector.onnx",
                "974686b591317739ff493f94c3f284c45c4b6b21f3acffac1ec7643b1ab481ae"
            },
        }
    );

    private static readonly List<ModelPackage> Fp16ModelPackages =
    [
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/MangaJaNai_V1_FP16_ONNX.zip",
            "c0b4fd81f6368c1c34d9fb82e2033bb00754abc5a2a7ed1bbddf0ac9fe6837f3",
            new Dictionary<string, string>
            {
                {
                    "2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "e9e841d06f5fe7e48f67fef7199b67b000702b3e2988f4af5d76a90a6a1acb9c"
                },
                {
                    "2x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "7f4cdecefd4ce2bdacaf551f2b5ca3c5bcb8bd10ddd917328de9b7917bd8919f"
                },
                {
                    "2x_MangaJaNai_1400p_V1_ESRGAN_70k.onnx",
                    "2f6eaae8cb6718468c83c34b44009d655914e7ffb32ffa9be43f87480e16e253"
                },
                {
                    "2x_MangaJaNai_1500p_V1_ESRGAN_90k.onnx",
                    "afd6e8e2765b5cdb4751f5d4e05428407a19f010a46dc6872ba05990376a7b4b"
                },
                {
                    "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
                    "436b6d3496b8c8063937ba42bec01aea9129ba650666ccca9c5658ac00d277f7"
                },
                {
                    "2x_MangaJaNai_1920p_V1_ESRGAN_70k.onnx",
                    "9c7cb1f6dffbdc8c17f634a0f4bce946958bf58b7b1295db273436e077fa6c82"
                },
                {
                    "2x_MangaJaNai_2048p_V1_ESRGAN_95k.onnx",
                    "67aa004413115673fd032cb2a995281e23c9ce35aa12405df23bfaf80b5021fa"
                },
                {
                    "4x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "35c34e4e846239421ec874c9bac1d8a0c4f5cccfbd39af028292ef0e438671e7"
                },
                {
                    "4x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "3bac04e572ee70efbcfcb5e03659426d6c921f9d5fc851118ab76880902dd2e8"
                },
                {
                    "4x_MangaJaNai_1400p_V1_ESRGAN_105k.onnx",
                    "5e34e632a790a7a22be3b67d106e26c88af28caa72ce57b0553b1ea3a01f6225"
                },
                {
                    "4x_MangaJaNai_1500p_V1_ESRGAN_105k.onnx",
                    "0207630b16f5898d502007e7cc7f9ae176607afad3fa106a0770fefbdf2f19de"
                },
                {
                    "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                    "dc945390d875e708307b171519bb1afb56e265a078ae9f74498de0f7c1fa02e7"
                },
                {
                    "4x_MangaJaNai_1920p_V1_ESRGAN_105k.onnx",
                    "9c66ec4b7dc80cfc12e9679399ea365f1ab15e812b99ef311151f89217ce55e6"
                },
                {
                    "4x_MangaJaNai_2048p_V1_ESRGAN_70k.onnx",
                    "5de460fe67f132269332b1f1efce48c28cb6e53951f476338ca1ce855eb95c8a"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V1_FP16_ONNX.zip",
            "1235d52be648aa4f40e18dd66ec5d02871a1527a292d5ca91f24ef013cc6feab",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V1_ESRGAN_120k.onnx",
                    "7cb8e9aa0f104fd36eed22feb638bc4fa39d541f30dd7944368cb024c71e4343"
                },
                {
                    "4x_IllustrationJaNai_V1_DAT2_190k.onnx",
                    "b700aa1a64b6a6fbdeba756be1badf323fda09bcec874c29f3f58b5579e38132"
                },
                {
                    "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
                    "d9f5adbe67a48534910064436e92baec7cd1feadce720b17ce9bd10dcddbeeb6"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/4x_IllustrationJaNai_V2standard_FP16_ONNX.zip",
            "59be9e46b32f21fa689ccd99c17f4ea89613a21aa946265e8c8003c2a25fe153",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V2standard_FDAT_M_unshuffle_40k.onnx",
                    "296a17a440ff2f0d2b1dd9d61031ad40a367bb34a980d05db3ca6871effbe152"
                },
                {
                    "4x_IllustrationJaNai_V2standard_DAT2_27k.onnx",
                    "77866ea12664c0e1c1a803ff6dc626a48d1bd1ef7285627fb612c24fb9c6f66b"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
                    "06f9af8df4fd5acc1f4e165b00eec94c5c4ea5a639292d8599ac17dcd0cb99c1"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_XL_18k.onnx",
                    "b3e0d9fa5e95c720567767f55d5d3177b65090962de1c7d0bfb159cf24788b78"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3denoise_FP16_ONNX.zip",
            "b38fd21c3d5b7e3f21a0844cb1012b7cad7b34e5fbb614cd687205fa76eec9bb",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16.onnx",
                    "b1a6d9137b2dd8215e5b12e3233605e942b115be0aa41d869be2d8ccaef58e54"
                },
                {
                    "2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16.onnx",
                    "1b96dd10e3241eeb76ce149342870f86e511ad861abba621b01d561cceca0f82"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_DAT2_27k_bf16.onnx",
                    "5c313112b090d8eba97314e809e9b5508a09dea55cb49152173f90447022c520"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_M_47k_fp16.onnx",
                    "0cdc6f2597931fcddf5f5e6cc01b071b9f6da890b16db4b34a178f2757dd6989"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_XL_32k_bf16.onnx",
                    "d68de1754384eb4cb42f4a2891e570d939c6590c712c880c0e297bce34383ae6"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3detail_FP16_ONNX.zip",
            "f0142a0781a3a9d9ae1eda4ed0af6a12300649719441a1a54b917e7ee3a02134",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3detail_FDAT_M_unshuffle_40k_fp16.onnx",
                    "ae263428f29eb775fa0a190de6edece9e1e56409162b6f29099f3c9986e84705"
                },
                {
                    "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
                    "77434949223b70bccd95869c1d52111397180cc9d23b18ef9585f9dc85c238e3"
                },
                {
                    "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
                    "ea850b31d42775e964ce4655e0710eeb058f0a27c35e2f7cf5fd6f52bcb21082"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
                    "e3e576f8822612c50a5967c36a87cdebf32470f206675cf6de7735d29e7c01ef"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx",
                    "cdab4d3c34b3bc77c618f6af8a73da76602f05b14c438561a49247440ab89301"
                },
                {
                    "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
                    "0d7de5ec99c6d2d2bc523132d72849900d858a90b4d5116d1329dae141d53357"
                },
            }
        ),
    ];

    private static readonly List<ModelPackage> Fp32ModelPackages =
    [
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/MangaJaNai_V1_ONNX.zip",
            "2f640c2debcd99618890a5b115e7d6790f2c3de110fc5e15bedcf622c2b4b190",
            new Dictionary<string, string>
            {
                {
                    "2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "c827cc5093c4b52137a045e13186a6156e17c956c2e093b8176a683784e8bd7f"
                },
                {
                    "2x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "97a0ea9069ac5f2e321c6a29ea128b94ddb62952acb9bc590d01f1ae43dab109"
                },
                {
                    "2x_MangaJaNai_1400p_V1_ESRGAN_70k.onnx",
                    "36bc722effe99547e1c8f7d07540c9f9c1661bd1c08f4856dbd12b5662568195"
                },
                {
                    "2x_MangaJaNai_1500p_V1_ESRGAN_90k.onnx",
                    "8b2ae07d0299888aec0eef5e0593b78aed1ebabe4796942bca35d8f103a52799"
                },
                {
                    "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
                    "787a24e17363c2eb148c8ba3967dbc02fb3c285bcf8ed6df3fd45b08e9305057"
                },
                {
                    "2x_MangaJaNai_1920p_V1_ESRGAN_70k.onnx",
                    "de2849f24f4007662315df231cea49597a6760cdc7c746f5db0d46dd4e8683af"
                },
                {
                    "2x_MangaJaNai_2048p_V1_ESRGAN_95k.onnx",
                    "807473a147a603fab5134fed6dcd5604a851da0dfd7412252a77622e83206320"
                },
                {
                    "4x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "41f44fab59abb4c67691e036e1770f96d2a60d7d0d4306b05ecc205fc5c82ef3"
                },
                {
                    "4x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "f78c4743ee609fe6580ff145fd201566693238bb9c831fb21d6da10565703a47"
                },
                {
                    "4x_MangaJaNai_1400p_V1_ESRGAN_105k.onnx",
                    "f9b2b67e786eee60fac72034b44f1ba24b291387ec446c809ac9e8e4b47ef353"
                },
                {
                    "4x_MangaJaNai_1500p_V1_ESRGAN_105k.onnx",
                    "faae3b6a2d9519f5302aa20859108b08107e9ee3bd295c8a22971a8e0097f8b7"
                },
                {
                    "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                    "095327848e672b1e7893d44d9161c72de60dffb1339fa64a639e3cbb3c6c6bf2"
                },
                {
                    "4x_MangaJaNai_1920p_V1_ESRGAN_105k.onnx",
                    "89db719c84f74bf205d6b13be9568e09b13b5c278c08c0d86f025e71009e22a8"
                },
                {
                    "4x_MangaJaNai_2048p_V1_ESRGAN_70k.onnx",
                    "6255e2fc797aa3662d2f8403772263ffea939ba3a6d828a0ffad14453f00d57b"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V1_ONNX.zip",
            "9696715934533d9f6ca824b0f8eec1057e6df381695daa0a58b0a6378ada1baa",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V1_ESRGAN_120k.onnx",
                    "4dbce88e5ec2141f0754d824b01e8fc6621b08b1c5064870c9c18b3a240890f4"
                },
                {
                    "4x_IllustrationJaNai_V1_DAT2_190k.onnx",
                    "8f31cbbe0b567928da459d5dcafe26ca52947aed054852a139ce6b4d1bd09beb"
                },
                {
                    "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
                    "ca27ada700d717d01492c7765c672bf6dc57df3395794ff152fdaeca9e9e082f"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/4x_IllustrationJaNai_V2standard_ONNX.zip",
            "41d38168f5fe7bb9272390c46223fc7ff251bbcdfcd779f78afd5799c15c82cd",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V2standard_FDAT_M_unshuffle_40k.onnx",
                    "8f5844e69102a1703b3cabe72cc3855b3830998b1f962c1131ea4bfa1e8b626b"
                },
                {
                    "4x_IllustrationJaNai_V2standard_DAT2_27k.onnx",
                    "f85903e977949067ffc6dc2362d5daad6bdf58c1c6de388ddc2db6aee8d2dc3f"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
                    "499cb918de68ca0af669720e28d0711e7f50b4897a34af8e379f1c0dcb25ac21"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_XL_18k.onnx",
                    "cc992a6615a2dba0ccaac348b489cbaf9f795e31aed142e30cd23ed289d39428"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3denoise_ONNX.zip",
            "d5cbeee6175b34357c1bb02dbab6408f2d7ba5e50919fc81c16a3928edfc4607",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16.onnx",
                    "c731f071ef59a823034950de567795f01412b9a64be6fee5f6ed7c2691863798"
                },
                {
                    "2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16.onnx",
                    "1b96dd10e3241eeb76ce149342870f86e511ad861abba621b01d561cceca0f82"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_DAT2_27k_bf16.onnx",
                    "2cb01660f310676124b1f8542fd5ca18546cf4f98cda88f68b2c086147be1618"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_M_47k_fp16.onnx",
                    "d148a4098c6a765dfedf2858440c45298ee5c3b52b432daa69f20796b297a401"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_XL_32k_bf16.onnx",
                    "ebaf02b4dfc54e44f620a978f3bc7643570347d42e5a6e95c5afaf22cc88c699"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3detail_ONNX.zip",
            "03853d2185ad53c2fe5d38de9ace8a0b2855c6a508baa1d86124a4a2cd289d6f",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3detail_FDAT_M_unshuffle_40k_fp16.onnx",
                    "f1de529a149c3290180e04421dd33e78b471392e2fc29ac382050ffd153a51ec"
                },
                {
                    "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
                    "77434949223b70bccd95869c1d52111397180cc9d23b18ef9585f9dc85c238e3"
                },
                {
                    "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
                    "02f42062e8d39933083c2d4dd0afe62a2fdb43413cf007ae86669a01b594c443"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
                    "d83e6c2863d525990e6050621d436b42652914cc9c63039249823a0721fbf58a"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx",
                    "5d3e6b3c5af465548a33370382ce75ceec0bf9167cd686da9703e883ff9da317"
                },
                {
                    "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
                    "ad25d22099d549fbd2dd62fc828420525c8cdda51df512ebedc5bbfa25eb2e10"
                },
            }
        ),
    ];

    private IReadOnlyList<ModelPackage> GetModelPackages()
    {
        var packages = sharedConfig.Value.ResolvedUseFp16 ? Fp16ModelPackages : Fp32ModelPackages;
        return [.. packages, PageBreakDetectorPackage];
    }

    private string ModelPath => sharedConfig.Value.ResolvedModelsDirectory;

    public async Task DownloadModelsIfNecessary(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ModelPath))
        {
            fileSystem.CreateDirectory(ModelPath);
        }

        foreach (var package in GetModelPackages())
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
