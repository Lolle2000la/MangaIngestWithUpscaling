using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetVips;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class OnnxUpscaleEngineTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_engine_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void SelectModel_DirectoryNotFound_ThrowsDirectoryNotFoundException()
    {
        string nonExistent = Path.Combine(_tempDir, "does_not_exist");
        Assert.Throws<DirectoryNotFoundException>(() =>
            OnnxUpscaleEngine.SelectModel(nonExistent, isGrayscale: true, height: 1200, scale: 2)
        );
    }

    [Fact]
    public void SelectModel_NoOnnxFiles_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            OnnxUpscaleEngine.SelectModel(_tempDir, isGrayscale: true, height: 1200, scale: 2)
        );
    }

    [Theory]
    [InlineData(1000, "2x_MangaJaNai_1200p_v1.onnx")]
    [InlineData(1250, "2x_MangaJaNai_1200p_v1.onnx")]
    [InlineData(1300, "2x_MangaJaNai_1300p_v1.onnx")]
    [InlineData(1400, "2x_MangaJaNai_1400p_v1.onnx")]
    [InlineData(1500, "2x_MangaJaNai_1500p_v1.onnx")]
    [InlineData(1600, "2x_MangaJaNai_1600p_v1.onnx")]
    [InlineData(1920, "2x_MangaJaNai_1920p_v1.onnx")]
    [InlineData(2200, "2x_MangaJaNai_2048p_v1.onnx")]
    public void SelectModel_Grayscale_SelectsTargetResolution(int height, string expectedFile)
    {
        string[] modelFiles =
        [
            "2x_MangaJaNai_1200p_v1.onnx",
            "2x_MangaJaNai_1300p_v1.onnx",
            "2x_MangaJaNai_1400p_v1.onnx",
            "2x_MangaJaNai_1500p_v1.onnx",
            "2x_MangaJaNai_1600p_v1.onnx",
            "2x_MangaJaNai_1920p_v1.onnx",
            "2x_MangaJaNai_2048p_v1.onnx",
            "2x_IllustrationJaNai_V3detail_v1.onnx",
        ];

        foreach (var file in modelFiles)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "dummy");
        }

        string selected = OnnxUpscaleEngine.SelectModel(
            _tempDir,
            isGrayscale: true,
            height: height,
            scale: 2
        );
        Assert.Equal(Path.Combine(_tempDir, expectedFile), selected);
    }

    [Fact]
    public void SelectModel_Color_PrefersDetailOverDenoiseAndStandard()
    {
        string[] modelFiles =
        [
            "2x_IllustrationJaNai_V1_v1.onnx",
            "2x_IllustrationJaNai_V2standard_v1.onnx",
            "2x_IllustrationJaNai_V3denoise_v1.onnx",
            "2x_IllustrationJaNai_V3detail_v1.onnx",
            "2x_MangaJaNai_1200p_v1.onnx",
        ];

        foreach (var file in modelFiles)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "dummy");
        }

        string selected = OnnxUpscaleEngine.SelectModel(
            _tempDir,
            isGrayscale: false,
            height: 1200,
            scale: 2
        );
        Assert.Equal(Path.Combine(_tempDir, "2x_IllustrationJaNai_V3detail_v1.onnx"), selected);
    }

    [Fact]
    public void SelectModel_Color_PrefersFdatMOverDat2Bf16()
    {
        string[] modelFiles =
        [
            "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
            "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
        ];

        foreach (var file in modelFiles)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "dummy");
        }

        string selected = OnnxUpscaleEngine.SelectModel(
            _tempDir,
            isGrayscale: false,
            height: 1200,
            scale: 4
        );
        Assert.Equal(
            Path.Combine(_tempDir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx"),
            selected
        );
    }

    [Fact]
    public void SelectModel_Grayscale_FallbackToGenericMangaJaNai()
    {
        File.WriteAllText(Path.Combine(_tempDir, "2x_MangaJaNai_generic.onnx"), "dummy");
        File.WriteAllText(Path.Combine(_tempDir, "2x_IllustrationJaNai_V1.onnx"), "dummy");

        string selected = OnnxUpscaleEngine.SelectModel(
            _tempDir,
            isGrayscale: true,
            height: 1200,
            scale: 2
        );
        Assert.Equal(Path.Combine(_tempDir, "2x_MangaJaNai_generic.onnx"), selected);
    }

    [Theory]
    [InlineData("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx")]
    [InlineData("4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx")]
    [InlineData("4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx")]
    public void SelectModelCandidates_OrdersTheFp16CopyBeforeTheFp32One(string fp16Name)
    {
        // Both precisions are installed where WebGPU is the provider, because it cannot run the
        // transformer architectures in fp16. The smaller file has to come first, so an accelerator
        // that can run it never pays for the fp32 copy, and a device that cannot still has one.
        string fp32Name = ModelFileNames.Resolve(fp16Name, ModelFileNames.Fp32Suffix);
        File.WriteAllText(Path.Combine(_tempDir, fp16Name), "dummy");
        File.WriteAllText(Path.Combine(_tempDir, fp32Name), "dummy");

        IReadOnlyList<string> candidates = OnnxUpscaleEngine.SelectModelCandidates(
            _tempDir,
            isGrayscale: false,
            height: 1600,
            scale: 4
        );

        Assert.True(
            candidates.ToList().IndexOf(Path.Combine(_tempDir, fp16Name))
                < candidates.ToList().IndexOf(Path.Combine(_tempDir, fp32Name)),
            "the fp16 copy should be preferred over the fp32 one"
        );
    }

    [Fact]
    public void SelectModel_IsTheFirstCandidate()
    {
        // Every path SelectModel used to return has to stay the first preference, so the
        // candidate list is what now feeds it rather than being a second opinion.
        string[] modelFiles =
        [
            "4x_MangaJaNai_1600p_v1.onnx",
            "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
            "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
            "4x_IllustrationJaNai_V3denoise_DAT2_27k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
            "4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
        ];
        foreach (var file in modelFiles)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "dummy");
        }

        Assert.Equal(
            OnnxUpscaleEngine.SelectModel(_tempDir, isGrayscale: true, height: 1600, scale: 4),
            OnnxUpscaleEngine.SelectModelCandidates(_tempDir, isGrayscale: true, height: 1600, 4)[0]
        );
        Assert.Equal(
            OnnxUpscaleEngine.SelectModel(_tempDir, isGrayscale: false, height: 1600, scale: 4),
            OnnxUpscaleEngine.SelectModelCandidates(_tempDir, isGrayscale: false, height: 1600, 4)[
                0
            ]
        );
    }

    [Fact]
    public void SelectModelCandidates_Color_KeepsAV1ModelAfterTheTransformerOnes()
    {
        // Regression: every 4x IllustrationJaNai transformer model returns NaN on WebGPU, so the
        // only model that survives on that provider is the V1 ESRGAN. It has to be reachable
        // after all of them are rejected, which it is only because the versions are ordered.
        string[] modelFiles =
        [
            "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
            "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
            "4x_IllustrationJaNai_V3denoise_DAT2_27k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_DAT2_28k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
            "4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx",
            "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
        ];
        foreach (var file in modelFiles)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "dummy");
        }

        IReadOnlyList<string> candidates = OnnxUpscaleEngine.SelectModelCandidates(
            _tempDir,
            isGrayscale: false,
            height: 1600,
            scale: 4
        );

        string v1 = Assert.Single(
            candidates,
            c => Path.GetFileName(c).Contains("V1_ESRGAN", StringComparison.OrdinalIgnoreCase)
        );
        int v1Rank = candidates.ToList().IndexOf(v1);
        foreach (string transformer in modelFiles.Where(f => !f.Contains("V1_ESRGAN")))
        {
            int rank = candidates.ToList().IndexOf(Path.Combine(_tempDir, transformer));
            Assert.True(
                rank >= 0 && rank < v1Rank,
                $"{transformer} should be preferred over {v1} but ranked {rank} vs {v1Rank}"
            );
        }
    }

    [Fact]
    public void SelectModelCandidates_IncludesEveryModelInTheDirectory()
    {
        // The fallback must never run out of candidates: if the last model also fails, the page
        // has to fail loudly rather than silently go black.
        string[] modelFiles =
        [
            "4x_MangaJaNai_1600p_v1.onnx",
            "4x_IllustrationJaNai_V1_ESRGAN_135k.onnx",
            "unrelated_model.onnx",
        ];
        foreach (var file in modelFiles)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "dummy");
        }

        IReadOnlyList<string> candidates = OnnxUpscaleEngine.SelectModelCandidates(
            _tempDir,
            isGrayscale: false,
            height: 1600,
            scale: 4
        );

        foreach (string file in modelFiles)
        {
            Assert.Contains(Path.Combine(_tempDir, file), candidates);
        }
    }

    [Fact]
    public void IsGrayscale_SingleBand_ReturnsTrue()
    {
        using var img = Image.Black(10, 10, bands: 1);
        Assert.True(OnnxUpscaleEngine.IsGrayscale(img));
    }

    [Fact]
    public void IsGrayscale_RgbEqualChannels_ReturnsTrue()
    {
        // 3 bands with uniform gray 128
        using var r = Image.Black(10, 10).Linear([1], [128]);
        using var g = Image.Black(10, 10).Linear([1], [128]);
        using var b = Image.Black(10, 10).Linear([1], [128]);
        using var rgb = r.Bandjoin(g).Bandjoin(b);

        Assert.True(OnnxUpscaleEngine.IsGrayscale(rgb));
    }

    [Fact]
    public void IsGrayscale_RgbColoredChannels_ReturnsFalse()
    {
        using var r = Image.Black(10, 10).Linear([1], [255]);
        using var g = Image.Black(10, 10).Linear([1], [0]);
        using var b = Image.Black(10, 10).Linear([1], [0]);
        using var rgb = r.Bandjoin(g).Bandjoin(b);

        Assert.False(OnnxUpscaleEngine.IsGrayscale(rgb));
    }

    [Fact]
    public void IsGrayscale_RgbJpegChromaNoise_ReturnsTrue()
    {
        // 100x100 image mostly 128 gray, but with minor channel divergence simulating JPEG compression noise (max diff = 5, mean diff < 0.1)
        byte[] rBytes = new byte[100 * 100];
        byte[] gBytes = new byte[100 * 100];
        byte[] bBytes = new byte[100 * 100];
        Array.Fill(rBytes, (byte)128);
        Array.Fill(gBytes, (byte)128);
        Array.Fill(bBytes, (byte)128);
        // Introduce small noise in a single pixel
        rBytes[0] = 133; // diff = 5

        using var r = Image.NewFromMemory(rBytes, 100, 100, 1, NetVips.Enums.BandFormat.Uchar);
        using var g = Image.NewFromMemory(gBytes, 100, 100, 1, NetVips.Enums.BandFormat.Uchar);
        using var b = Image.NewFromMemory(bBytes, 100, 100, 1, NetVips.Enums.BandFormat.Uchar);
        using var rgb = r.Bandjoin(g).Bandjoin(b);

        Assert.True(OnnxUpscaleEngine.IsGrayscale(rgb));
    }

    [Fact]
    public async Task UpscaleFileAsync_MissingInputFile_ThrowsFileNotFoundException()
    {
        var sessionFactory = Substitute.For<IOnnxSessionFactory>();
        var config = Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir });
        var engine = new OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            engine.UpscaleFileAsync(
                Path.Combine(_tempDir, "missing.png"),
                Path.Combine(_tempDir, "out.png"),
                scale: 2,
                CompressionFormat.Webp,
                quality: 80,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task UpscaleCbzAsync_MissingInputFile_ThrowsFileNotFoundException()
    {
        var sessionFactory = Substitute.For<IOnnxSessionFactory>();
        var config = Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir });
        var engine = new OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            engine.UpscaleCbzAsync(
                Path.Combine(_tempDir, "missing.cbz"),
                Path.Combine(_tempDir, "out.cbz"),
                scale: 2,
                CompressionFormat.Webp,
                quality: 80,
                progress: null,
                CancellationToken.None
            )
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpscaleFileAsync_UpscalesImageSuccessfully()
    {
        // The full path from a file on disk to an upscaled file on disk: vips decode, RGB flatten,
        // the tiler and the encoder — the layers above the ones OnnxTilerTests covers individually.
        // Both inputs used to come from the gitignored test_data/ directory, which is why this test
        // never ran on a clean checkout. A generated model and a generated image exercise the same
        // path, so the seam between the tiler and the image codecs stays covered.
        const int width = 64;
        const int height = 90;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(_tempDir, width, height);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var sessionFactory = new OnnxSessionFactory(
            config,
            NullLogger<OnnxSessionFactory>.Instance
        );
        var engine = new OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string outputPath = Path.Combine(_tempDir, "upscaled.webp");
        await engine.UpscaleFileAsync(
            inputPath,
            outputPath,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            CancellationToken.None
        );

        Assert.True(File.Exists(outputPath));
        using var outImg = Image.NewFromFile(outputPath);
        Assert.Equal(width * scale, outImg.Width);
        Assert.Equal(height * scale, outImg.Height);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpscaleFileAsync_EncodesThroughATempAndLeavesNoPartialBehind()
    {
        // The encode is staged next to the destination and moved on completion, so a codec or disk
        // failure cannot leave a truncated image where the page should be. This pins the two
        // observable consequences: the destination is replaced atomically, and the staging file does
        // not survive the call.
        const int width = 64;
        const int height = 90;
        const int scale = 2;
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateTestJpeg(_tempDir, width, height);
        string outputPath = Path.Combine(_tempDir, "upscaled.webp");

        // A previous, complete output that the new one must replace.
        File.WriteAllText(outputPath, "previous-output");

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var sessionFactory = new OnnxSessionFactory(
            config,
            NullLogger<OnnxSessionFactory>.Instance
        );
        var engine = new OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        await engine.UpscaleFileAsync(
            inputPath,
            outputPath,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            TestContext.Current.CancellationToken
        );

        using var outImg = Image.NewFromFile(outputPath);
        Assert.Equal(width * scale, outImg.Width);
        Assert.Equal(height * scale, outImg.Height);

        Assert.Empty(Directory.GetFiles(_tempDir, "*.part-*"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpscaleCbzAsync_SameStemInputs_KeepsBothPages()
    {
        // 001.jpg and 001.png both map to 001.webp. A zip allows duplicate entry names and a
        // name-keyed reader keeps only one of them, so the second page must be disambiguated
        // rather than silently overwritten.
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string input = CreateImageCbz(
            _tempDir,
            "same-stem.cbz",
            ("001.jpg", 64, 90),
            ("001.png", 48, 60)
        );
        string output = Path.Combine(_tempDir, "same-stem-out.cbz");

        using OnnxSessionFactory sessionFactory = CreateCpuSessionFactory(_tempDir);
        OnnxUpscaleEngine engine = CreateEngine(sessionFactory, _tempDir);
        await engine.UpscaleCbzAsync(
            input,
            output,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            progress: null,
            CancellationToken.None
        );

        using ZipArchive archive = ZipFile.OpenRead(output);
        List<ZipArchiveEntry> pages = archive.Entries.Where(e => e.Name.Length > 0).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal(2, pages.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count());

        // Both source contents survived: the two pages keep their distinct aspect ratios.
        List<(int Width, int Height)> sizes = pages.Select(ReadEntrySize).ToList();
        Assert.Contains((64 * scale, 90 * scale), sizes);
        Assert.Contains((48 * scale, 60 * scale), sizes);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpscaleCbzAsync_OrdersPagesNaturally()
    {
        // Ordinal order would give 1, 10, 2; page sequence is content-critical, so it must be
        // 1, 2, 10.
        const int scale = 2;
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string input = CreateImageCbz(
            _tempDir,
            "order.cbz",
            ("1.png", 20, 30),
            ("2.png", 20, 30),
            ("10.png", 20, 30)
        );
        string output = Path.Combine(_tempDir, "order-out.cbz");

        using OnnxSessionFactory sessionFactory = CreateCpuSessionFactory(_tempDir);
        OnnxUpscaleEngine engine = CreateEngine(sessionFactory, _tempDir);
        await engine.UpscaleCbzAsync(
            input,
            output,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            progress: null,
            CancellationToken.None
        );

        using ZipArchive archive = ZipFile.OpenRead(output);
        List<string> names = archive
            .Entries.Where(e => e.Name.Length > 0)
            .Select(e => e.FullName)
            .ToList();
        Assert.Equal(["1.webp", "2.webp", "10.webp"], names);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpscaleCbzAsync_UnloadableCandidate_IsSkippedNotFatal()
    {
        // Candidate selection appends every .onnx as a last resort, so one corrupt file can be
        // probed. Its probe failure must skip it, not abort the page, when a working candidate
        // follows. "aaa_" sorts it ahead of the generated model so it is the one probed first.
        const int scale = 2;
        File.WriteAllBytes(
            Path.Combine(_tempDir, "aaa_corrupt.onnx"),
            System.Text.Encoding.UTF8.GetBytes("this is not an onnx model")
        );
        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string input = CreateImageCbz(_tempDir, "bad-model.cbz", ("1.png", 32, 40));
        string output = Path.Combine(_tempDir, "bad-model-out.cbz");

        using OnnxSessionFactory sessionFactory = CreateCpuSessionFactory(_tempDir);
        OnnxUpscaleEngine engine = CreateEngine(sessionFactory, _tempDir);
        await engine.UpscaleCbzAsync(
            input,
            output,
            scale,
            CompressionFormat.Webp,
            quality: 80,
            progress: null,
            CancellationToken.None
        );

        Assert.True(File.Exists(output));
        using ZipArchive archive = ZipFile.OpenRead(output);
        ZipArchiveEntry page = Assert.Single(archive.Entries, e => e.Name.Length > 0);
        Assert.Equal((32 * scale, 40 * scale), ReadEntrySize(page));
    }

    /// <summary>
    /// Builds a CPU engine against a real <see cref="OnnxSessionFactory"/> so the whole path —
    /// decode, probe, inference, encode — runs, as it does in production.
    /// </summary>
    private static OnnxUpscaleEngine CreateEngine(
        OnnxSessionFactory sessionFactory,
        string modelsDirectory
    )
    {
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = modelsDirectory, UseCPU = true }
        );
        return new OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        );
    }

    private static OnnxSessionFactory CreateCpuSessionFactory(string modelsDirectory)
    {
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = modelsDirectory, UseCPU = true }
        );
        return new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
    }

    /// <summary>
    /// Writes a CBZ whose entries are fresh noisy images of the requested size. The source files
    /// live in a subdirectory so they cannot be mistaken for models by candidate selection.
    /// </summary>
    private static string CreateImageCbz(
        string directory,
        string fileName,
        params (string EntryName, int Width, int Height)[] pages
    )
    {
        string sourceDir = Path.Combine(directory, $"cbz_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        string path = Path.Combine(directory, fileName);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryName, int width, int height) in pages)
        {
            using var noise = Image
                .Gaussnoise(width, height, mean: 128, sigma: 40)
                .Cast(Enums.BandFormat.Uchar);
            using var rgb = noise.Bandjoin(noise).Bandjoin(noise);
            string source = Path.Combine(
                sourceDir,
                $"{Guid.NewGuid():N}{Path.GetExtension(entryName)}"
            );
            rgb.WriteToFile(source);
            archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
        }

        return path;
    }

    private static (int Width, int Height) ReadEntrySize(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var image = Image.NewFromBuffer(buffer.ToArray());
        return (image.Width, image.Height);
    }

    /// <summary>
    /// Writes a non-uniform JPEG so the decoded bytes are not all identical: a constant image would
    /// pass a scaling bug that produced a constant image.
    /// </summary>
    private static string CreateTestJpeg(string directory, int width, int height)
    {
        using var noise = Image
            .Gaussnoise(width, height, mean: 128, sigma: 40)
            .Cast(Enums.BandFormat.Uchar);
        using var rgb = noise.Bandjoin(noise).Bandjoin(noise);
        string path = Path.Combine(directory, "input.jpg");
        rgb.WriteToFile(path);
        return path;
    }
}
