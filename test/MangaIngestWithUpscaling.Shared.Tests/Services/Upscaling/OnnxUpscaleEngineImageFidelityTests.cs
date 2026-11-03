using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NetVips;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// End-to-end checks that the engine preserves hue on transparent pages and keeps the luminance
/// of a grayscale page. Both behaviours used to be lost silently — the first by compositing the
/// alpha onto black, the second by keeping only the red plane of a colour model's output — so the
/// assertions are made on the bytes actually written to disk.
/// </summary>
public class OnnxUpscaleEngineImageFidelityTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxUpscaleEngineImageFidelityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx_engine_fidelity_{Guid.NewGuid():N}");
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
    [Trait("Category", "Integration")]
    public async Task UpscaleFileAsync_TransparentRegion_KeepsTheStoredColourInsteadOfBlack()
    {
        // The engine used to Flatten() a page with alpha before the model saw it. libvips'
        // Flatten() composites onto black, and the output is re-created as a 3-band image, so the
        // transparent half was baked into the upscaled RGB as black. The detector already fixed
        // exactly this class of bug by dropping the alpha band instead, so the upscaler now does
        // the same: the colour stored under the transparent pixel must survive, and it must match
        // the upscaled neighbourhood of the same colour.
        const int width = 32;
        const int height = 16;
        const int scale = 2;
        const byte r = 200;
        const byte g = 100;
        const byte b = 50;

        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateHalfTransparentPng(width, height, r, g, b);
        string outputPath = Path.Combine(_tempDir, "upscaled-alpha.png");

        using OnnxSessionFactory sessionFactory = CreateCpuSessionFactory(_tempDir);
        OnnxUpscaleEngine engine = CreateEngine(sessionFactory, _tempDir);
        await engine.UpscaleFileAsync(
            inputPath,
            outputPath,
            scale,
            CompressionFormat.Png,
            quality: null,
            TestContext.Current.CancellationToken
        );

        using var outImg = Image.NewFromFile(outputPath);
        Assert.Equal(width * scale, outImg.Width);
        Assert.Equal(height * scale, outImg.Height);

        // A point well inside each half, at mid-height: the left half was opaque, the right half
        // was fully transparent with the same colour stored underneath.
        (byte R, byte G, byte B) opaque = ReadPixel(outImg, 16, height);
        (byte R, byte G, byte B) transparent = ReadPixel(outImg, 48, height);

        Assert.Equal((r, g, b), opaque);

        // Not black: the colour was present, so flattening onto black is what produced 0,0,0 here.
        Assert.True(
            transparent.R > 150,
            $"the transparent region came out black ({transparent.R},{transparent.G},{transparent.B}); "
                + "the stored colour was lost"
        );

        // It matches the stored colour's own upscaled neighbourhood, so the assertion is on the
        // colour rather than on a particular encoder rounding.
        Assert.Equal(opaque, transparent);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpscaleFileAsync_GrayscalePage_UsesLuminanceNotTheRedPlane()
    {
        // A grayscale page can fall back to a colour model — the ladder's last rung is an
        // IllustrationJaNai model — and that model's three channels then differ. Keeping band 0
        // turned the residual imbalance into a brightness error; the saved page was the red plane
        // of a colour output. It has to be the luminance.
        const int width = 32;
        const int height = 16;
        const int scale = 2;
        const byte r = 200;
        const byte g = 100;
        const byte b = 50;

        TinyOnnxModel.WriteNearestUpscaler(_tempDir, scale);
        string inputPath = CreateFlatGrayscalePng(width, height, 128);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        using var sessionFactory = new OnnxSessionFactory(
            config,
            NullLogger<OnnxSessionFactory>.Instance
        );
        var engine = new FixedOutputEngine(r, g, b, sessionFactory, config);

        string outputPath = Path.Combine(_tempDir, "upscaled-grayscale.png");
        await engine.UpscaleFileAsync(
            inputPath,
            outputPath,
            scale,
            CompressionFormat.Png,
            quality: null,
            TestContext.Current.CancellationToken
        );

        using var outImg = Image.NewFromFile(outputPath);
        int value = ReadFirstBand(outImg, (width * scale) / 2, (height * scale) / 2);

        // The expected luminance, computed from first principles (sRGB -> linear, Rec.709 Y,
        // linear -> sRGB), not by calling the same conversion the engine uses.
        int expected = (int)Math.Round(GammaAwareRec709Luminance(r, g, b));
        Assert.InRange(value, expected - 2, expected + 2);

        // It is neither the red plane nor a plain channel average: the imbalance is deliberate.
        Assert.True(
            value < r - 10,
            $"the grayscale page kept the red plane ({value} vs red {r}) instead of the luminance"
        );
        Assert.NotEqual((r + g + b) / 3, value);
    }

    /// <summary>
    /// The reference luminance: sRGB values to linear, the Rec.709 weights, then back to sRGB.
    /// This is what libvips' <c>colourspace</c> to <c>Bw</c> computes; it is independent of the
    /// engine's own call so a regression to a different weighting still fails.
    /// </summary>
    private static double GammaAwareRec709Luminance(byte r, byte g, byte b)
    {
        double linear =
            0.2126 * SrgbToLinear(r) + 0.7152 * SrgbToLinear(g) + 0.0722 * SrgbToLinear(b);
        return LinearToSrgb(linear) * 255.0;
    }

    private static double SrgbToLinear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double c) =>
        c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;

    /// <summary>
    /// Writes an RGBA PNG whose left half is opaque and whose right half is fully transparent,
    /// with <paramref name="r"/>… stored under the transparent pixels. A PNG keeps the colour
    /// underneath a zero alpha, which is exactly the case <c>Flatten()</c> used to black out.
    /// </summary>
    private string CreateHalfTransparentPng(int width, int height, byte r, byte g, byte b)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = (byte)(x < width / 2 ? 255 : 0);
            }
        }

        using var image = Image
            .NewFromMemory(pixels, width, height, 4, Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        string path = Path.Combine(_tempDir, "input-rgba.png");
        image.Pngsave(path);
        return path;
    }

    private string CreateFlatGrayscalePng(int width, int height, byte value)
    {
        byte[] pixels = new byte[width * height * 3];
        Array.Fill(pixels, value);
        using var image = Image
            .NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        string path = Path.Combine(_tempDir, "input-grayscale.png");
        image.Pngsave(path);
        return path;
    }

    private static (byte R, byte G, byte B) ReadPixel(Image image, int x, int y)
    {
        using var crop = image.Crop(x, y, 1, 1);
        using var uchar =
            crop.Format == Enums.BandFormat.Uchar ? crop.Copy() : crop.Cast(Enums.BandFormat.Uchar);
        byte[] pixel = uchar.WriteToMemory<byte>();
        return (pixel[0], pixel[1], pixel[2]);
    }

    private static byte ReadFirstBand(Image image, int x, int y)
    {
        using var crop = image.Crop(x, y, 1, 1);
        using var uchar =
            crop.Format == Enums.BandFormat.Uchar ? crop.Copy() : crop.Cast(Enums.BandFormat.Uchar);
        return uchar.WriteToMemory<byte>()[0];
    }

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
    /// An engine whose tiler call is replaced by a fixed three-channel output, through the seam the
    /// engine exposes for it. This isolates the grayscale save from the model, so the test can pin
    /// the imbalance the model is allowed to introduce without shipping such a model.
    /// </summary>
    private sealed class FixedOutputEngine(
        byte r,
        byte g,
        byte b,
        IOnnxSessionFactory sessionFactory,
        IOptions<UpscalerConfig> config
    )
        : OnnxUpscaleEngine(
            sessionFactory,
            config,
            Substitute.For<IDeviceMemoryCalibrator>(),
            NullLogger<OnnxUpscaleEngine>.Instance
        )
    {
        protected internal override byte[] UpscaleViaTiler(
            byte[] rgbBytes,
            int width,
            int height,
            int scale,
            InferenceSession session,
            OnnxTiler.TileSplit split,
            CancellationToken cancellationToken
        )
        {
            byte[] output = new byte[width * scale * height * scale * 3];
            for (int i = 0; i < output.Length; i += 3)
            {
                output[i] = r;
                output[i + 1] = g;
                output[i + 2] = b;
            }

            return output;
        }
    }
}
