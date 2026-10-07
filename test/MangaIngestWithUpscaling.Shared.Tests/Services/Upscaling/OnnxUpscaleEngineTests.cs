using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
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
    public async Task UpscaleFileAsync_MissingInputFile_ThrowsFileNotFoundException()
    {
        var sessionFactory = Substitute.For<IOnnxSessionFactory>();
        var config = Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir });
        var engine = new OnnxUpscaleEngine(
            sessionFactory,
            config,
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
    public async Task UpscaleFileAsync_RealOnnxModel_UpscalesImageSuccessfully()
    {
        string? repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);
        string modelsDir = Path.Combine(repoRoot, "test_data", "models");
        string testInput = Path.Combine(repoRoot, "test_data", "test_page.jpg");
        if (!Directory.Exists(modelsDir) || !File.Exists(testInput))
        {
            return;
        }

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = modelsDir, UseCPU = true }
        );
        using var sessionFactory = new OnnxSessionFactory(
            config,
            NullLogger<OnnxSessionFactory>.Instance
        );
        var engine = new OnnxUpscaleEngine(
            sessionFactory,
            config,
            NullLogger<OnnxUpscaleEngine>.Instance
        );

        string outputPath = Path.Combine(_tempDir, "upscaled.webp");
        await engine.UpscaleFileAsync(
            testInput,
            outputPath,
            scale: 2,
            CompressionFormat.Webp,
            quality: 80,
            CancellationToken.None
        );

        Assert.True(File.Exists(outputPath));
        using var outImg = Image.NewFromFile(outputPath);
        Assert.Equal(836 * 2, outImg.Width);
        Assert.Equal(1187 * 2, outImg.Height);
    }

    private static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "MangaIngestWithUpscaling.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
