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
            "d1e7594f1f074824eb67b5e30f88cc0c741fa54cb503a9d6301d7948d1a97f63",
            new Dictionary<string, string>
            {
                {
                    "2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "90cf432d8a333e7f8704afa5c03905e3d2d82844e8e550b4a36a6288f3f8f4df"
                },
                {
                    "2x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "9ed59dbc9c665e5626bacdad4747d3e9e2965d2bd9d3f98c3e3bb6414c0dc50c"
                },
                {
                    "2x_MangaJaNai_1400p_V1_ESRGAN_70k.onnx",
                    "412d35c10eca9d6c5c734d2dbba3cd739fb6f4d6abe18b79d76253a69a6aa929"
                },
                {
                    "2x_MangaJaNai_1500p_V1_ESRGAN_90k.onnx",
                    "0b0698556a4394ba3fb561590ed0114f8686bbe1a4029d949ae6d93305740abf"
                },
                {
                    "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
                    "cdc3a4e36acd07ece3837f7bd4f3ca66db8e86064bd9d9ef9697690178137c81"
                },
                {
                    "2x_MangaJaNai_1920p_V1_ESRGAN_70k.onnx",
                    "f627c925d7dd2fb7be8b5123e656fa84b37cf0bf383fef17b7f9cda941da10d8"
                },
                {
                    "2x_MangaJaNai_2048p_V1_ESRGAN_95k.onnx",
                    "92a9ea379c29a00bd9a8127b5749e1648b1003a64522b0d943766b195786de44"
                },
                {
                    "4x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
                    "8be065399a22ef79100adf7b09fac8a0d65e90e92b881f55bbbea8f99661fd51"
                },
                {
                    "4x_MangaJaNai_1300p_V1_ESRGAN_75k.onnx",
                    "3bdaee0e2cb064ea09098262275de339a7af0c5befcd1f79e327f56871d87a56"
                },
                {
                    "4x_MangaJaNai_1400p_V1_ESRGAN_105k.onnx",
                    "04e7b24ff70e46516000c5058870aa03df4a9a5d478527be628752c6ec57d0a6"
                },
                {
                    "4x_MangaJaNai_1500p_V1_ESRGAN_105k.onnx",
                    "15035454241bc417eedaad5944bdab0ea9ac9083aeca959de0de442f5d299986"
                },
                {
                    "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                    "5b1fca557cbaedd6727832b4ee7afda4ecd3f26fe578a2412ed3117e16344744"
                },
                {
                    "4x_MangaJaNai_1920p_V1_ESRGAN_105k.onnx",
                    "87556590b33c2c42e99428b31bd8df6c1fa1c1afd3137572ba7f919f31ca15ea"
                },
                {
                    "4x_MangaJaNai_2048p_V1_ESRGAN_70k.onnx",
                    "76ae3d8493d95f3affcbb1ca065ffcb5e0a2c5334ea140d41722435f04764ec7"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V1_FP16_ONNX.zip",
            "ac24d048470d5856ead1f79efb7b48ac92228611d5e7c993e01363c8fecf38e6",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V1_ESRGAN_120k.onnx",
                    "b25c6078a7fcab756807c1df76ba3e536aff49f23629aa1c41a0388425552407"
                },
                {
                    "4x_IllustrationJaNai_V1_DAT2_190k.onnx",
                    "be50647a0d87eeba9bf9a378c33433523cbcc5f79a904bd79976eee7d85f0515"
                },
                {
                    "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
                    "2dda30cd00a36b0e3699e7e65cff93b8bf815902e8cea717485be5b36a78d8fb"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/4x_IllustrationJaNai_V2standard_FP16_ONNX.zip",
            "ada8176c6184dc2de732c9c795150b064015578782d8bcedc9f6d513769e324d",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V2standard_FDAT_M_unshuffle_40k.onnx",
                    "4e7b92b357b90dbba37e7c5f834e7156c481427c31f392e431d0a3f0383490bc"
                },
                {
                    "4x_IllustrationJaNai_V2standard_DAT2_27k.onnx",
                    "d47106f9f298adcbadef75ca4aeef50e61588ca847a14783633d14a11af7a706"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
                    "541f651f52844fa4486f09cae5d2f404ca1f76a3dae7f30d1114d843de2dda2f"
                },
                {
                    "4x_IllustrationJaNai_V2standard_FDAT_XL_18k.onnx",
                    "17148f7e0ff82137e2d31387ecbb2550a804ed8a8b9970e63580c684f20196ca"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3denoise_FP16_ONNX.zip",
            "921fc95c8be8ed4c59242dfcd009f534b9225e45058c4c451d43ef3c7accc103",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16.onnx",
                    "0678b9f53a22ec7262b666c4c6922a71e8cde6c757b3407b355adfe9fa006f70"
                },
                {
                    "2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16.onnx",
                    "d29ac4cef6adf66ca72fe1757d20f57798351f1d4b737c837ad35a9fd30a8633"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_DAT2_27k_bf16.onnx",
                    "ee5199efd82f172d2de4b7b36986eafc968e1e752ce825b592bf81990f0e7855"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_M_47k_fp16.onnx",
                    "30d1ef0c597fbd1a980975868883861915ddbdd25f4e469ff776e679759bd9a6"
                },
                {
                    "4x_IllustrationJaNai_V3denoise_FDAT_XL_32k_bf16.onnx",
                    "11c450291aa9b20c7a3303170187ae5cb97e65f81065e3bf375aeadbfa65ce7d"
                },
            }
        ),
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/IllustrationJaNai_V3detail_FP16_ONNX.zip",
            "c60431c918dfde1b3d502bf11efafde5caf5e18c3c756d7f5db9788e0757eb94",
            new Dictionary<string, string>
            {
                {
                    "2x_IllustrationJaNai_V3detail_FDAT_M_unshuffle_40k_fp16.onnx",
                    "4b12a4da3be2e198bc826ae0881ee91bb56ade3c598754ee5433596e65cc7de9"
                },
                {
                    "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
                    "040f3d24bcb26cead6548e39972a9165775174d1b297ee404aefbda1a8380b5d"
                },
                {
                    "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
                    "825b50500a4b7bc20fe7853ca78a389005a61aeb90b2d02952536a0bb839867e"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
                    "585b6af155c04283404a0977eb5dd0b0cc2194fecce345792ae44112374f83fd"
                },
                {
                    "4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx",
                    "a13a4f79262f501ded148c2c83bc328fab5795673062e71123542fb71e80fb7e"
                },
                {
                    "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
                    "ce5bfd221954d128c4ed3278ea6a78f47baa492099dd431e4231a70354ec32b7"
                },
            }
        ),
    ];

    private static readonly List<ModelPackage> Fp32ModelPackages =
    [
        new(
            "https://github.com/Lolle2000la/MangaJaNai/releases/download/v3.0.0-onnx/MangaJaNai_V1_ONNX.zip",
            "cefaafc3837e7fe9b7109d9654086920407d1300943da5eda136616b5265a571",
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
            "96df0b234644a472f8397a6beee039b8fc9aff734dc979e5f0af61fb55d6dfe1",
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
            "c5a391ad49cb5dd72dcfdd7129079d7fc4a56be3fa9b2c083f934b82968fde5a",
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
            "7c19466a2945de2e0aca743f16604399476dc31a9f21239ce3d1678d04c58920",
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
            "28bbc34d492f9a378c65f705297b4e961d97c45b105692ce1e0eebcbca6f42ba",
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
        var packages = sharedConfig.Value.UseFp16 ? Fp16ModelPackages : Fp32ModelPackages;
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
