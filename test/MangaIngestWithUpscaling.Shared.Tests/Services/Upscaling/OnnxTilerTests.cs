using System.Reflection;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class OnnxTilerTests : IDisposable
{
    private const string NoGpuMemoryReporting =
        "no GPU or driver memory source is available in this environment, so GetGpuVramInfo "
        + "correctly returns (0, 0, 0) and there is no VRAM relationship to assert about";

    private readonly List<string> _created = [];

    [Fact]
    public void Blender_ImpossibleOutputSize_IsAnOutOfMemoryException()
    {
        // 40000 x 40000 x 3 is 4.8 GB: past int.MaxValue. The int product used to wrap, so the array
        // was asked either for a negative length — an OverflowException that IsMemoryException does
        // not recognise, which skipped the halve-and-retry recovery — or for a wrapped-but-positive
        // one that then had every row offset wrapped too.
        var ex = Assert.Throws<OutOfMemoryException>(() =>
            new TileBlender(40000, 40000, 3, BlendDirection.Vertical)
        );

        Assert.True(
            OnnxTiler.IsMemoryException(ex),
            "the halve-and-retry recovery must recognise an impossible output size as memory pressure"
        );
    }

    [Fact]
    public void ExtractCrop_ImpossibleCropSize_IsAnOutOfMemoryException()
    {
        var ex = Assert.Throws<OutOfMemoryException>(() =>
            OnnxTiler.ExtractCrop(new byte[3], srcWidth: 1, x: 0, y: 0, w: 40000, h: 40000)
        );

        Assert.True(OnnxTiler.IsMemoryException(ex));
    }

    [Fact]
    public void ExtractCrop_ExtractsCorrectRegion()
    {
        // 4x4 RGB image (16 pixels, 48 bytes)
        int srcWidth = 4;
        int srcHeight = 4;
        byte[] src = new byte[srcWidth * srcHeight * 3];

        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                int idx = (y * srcWidth + x) * 3;
                src[idx] = (byte)x;
                src[idx + 1] = (byte)y;
                src[idx + 2] = (byte)(x + y);
            }
        }

        // Crop 2x2 region from (1, 1)
        int cropX = 1;
        int cropY = 1;
        int cropW = 2;
        int cropH = 2;

        byte[] crop = OnnxTiler.ExtractCrop(src, srcWidth, cropX, cropY, cropW, cropH);

        Assert.Equal(cropW * cropH * 3, crop.Length);

        // Top-left pixel of crop corresponds to (1, 1) in src
        Assert.Equal(1, crop[0]);
        Assert.Equal(1, crop[1]);
        Assert.Equal(2, crop[2]);

        // Top-right pixel of crop corresponds to (2, 1) in src
        Assert.Equal(2, crop[3]);
        Assert.Equal(1, crop[4]);
        Assert.Equal(3, crop[5]);

        // Bottom-left pixel of crop corresponds to (1, 2) in src
        Assert.Equal(1, crop[6]);
        Assert.Equal(2, crop[7]);
        Assert.Equal(3, crop[8]);

        // Bottom-right pixel of crop corresponds to (2, 2) in src
        Assert.Equal(2, crop[9]);
        Assert.Equal(2, crop[10]);
        Assert.Equal(4, crop[11]);
    }

    [Fact]
    public void ExtractCrop_FullImage_CopiesIdenticalContent()
    {
        int w = 5;
        int h = 7;
        byte[] src = new byte[w * h * 3];
        new Random(42).NextBytes(src);

        byte[] crop = OnnxTiler.ExtractCrop(src, w, 0, 0, w, h);
        Assert.Equal(src, crop);
    }

    [Fact]
    public void HalfSinBlend_MatchesMathematicalDefinition()
    {
        Assert.Equal(0f, TileBlender.HalfSinBlend(0f), 4);
        Assert.Equal(0f, TileBlender.HalfSinBlend(0.2f), 4);
        Assert.Equal(0f, TileBlender.HalfSinBlend(0.25f), 4);
        Assert.Equal(0.5f, TileBlender.HalfSinBlend(0.5f), 4);
        Assert.Equal(1f, TileBlender.HalfSinBlend(0.75f), 4);
        Assert.Equal(1f, TileBlender.HalfSinBlend(0.85f), 4);
        Assert.Equal(1f, TileBlender.HalfSinBlend(1f), 4);

        // Monotonicity check
        float prev = 0f;
        for (int i = 0; i <= 100; i++)
        {
            float t = i / 100f;
            float val = TileBlender.HalfSinBlend(t);
            Assert.True(val >= prev, $"Value at {t} ({val}) should be >= previous ({prev})");
            Assert.InRange(val, 0f, 1f);
            prev = val;
        }
    }

    [Fact]
    public void GetBlendWeights_HandlesEdgeCases()
    {
        Assert.Empty(TileBlender.GetBlendWeights(0));
        Assert.Equal([0.5f], TileBlender.GetBlendWeights(1));

        float[] weights = TileBlender.GetBlendWeights(32);
        Assert.Equal(32, weights.Length);
        Assert.Equal(0f, weights[0], 4);
        Assert.Equal(1f, weights[^1], 4);
    }

    [Fact]
    public void TileBlender_Horizontal_TwoTiles_SeamlessReconstruction()
    {
        int width = 100;
        int height = 50;
        byte[] src = CreateTestImage(width, height);

        // Split into 2 horizontal tiles (50px core each, 16px overlap)
        int tile1W = 66; // [0, 66)
        int tile2W = 66; // [34, 100)
        byte[] tile1 = OnnxTiler.ExtractCrop(src, width, 0, 0, tile1W, height);
        byte[] tile2 = OnnxTiler.ExtractCrop(src, width, 34, 0, tile2W, height);

        var blender = new TileBlender(width, height, 3, BlendDirection.Horizontal);
        blender.AddTile(tile1, tile1W, height, new TileOverlap(0, 16));
        blender.AddTile(tile2, tile2W, height, new TileOverlap(16, 0));

        byte[] result = blender.GetResult();
        Assert.Equal(src, result);
    }

    [Fact]
    public void TileBlender_Horizontal_ThreeTiles_SeamlessReconstruction()
    {
        int width = 150;
        int height = 40;
        byte[] src = CreateTestImage(width, height);

        // 3 tiles: cores of 50px, overlap 16px
        int t1W = 66; // [0, 66)
        int t2W = 82; // [34, 116)
        int t3W = 66; // [84, 150)

        byte[] t1 = OnnxTiler.ExtractCrop(src, width, 0, 0, t1W, height);
        byte[] t2 = OnnxTiler.ExtractCrop(src, width, 34, 0, t2W, height);
        byte[] t3 = OnnxTiler.ExtractCrop(src, width, 84, 0, t3W, height);

        var blender = new TileBlender(width, height, 3, BlendDirection.Horizontal);
        blender.AddTile(t1, t1W, height, new TileOverlap(0, 16));
        blender.AddTile(t2, t2W, height, new TileOverlap(16, 16));
        blender.AddTile(t3, t3W, height, new TileOverlap(16, 0));

        byte[] result = blender.GetResult();
        Assert.Equal(src, result);
    }

    [Fact]
    public void TileBlender_Vertical_SeamlessReconstruction()
    {
        int width = 40;
        int height = 100;
        byte[] src = CreateTestImage(width, height);

        int r1H = 66; // [0, 66)
        int r2H = 66; // [34, 100)
        byte[] r1 = OnnxTiler.ExtractCrop(src, width, 0, 0, width, r1H);
        byte[] r2 = OnnxTiler.ExtractCrop(src, width, 0, 34, width, r2H);

        var blender = new TileBlender(width, height, 3, BlendDirection.Vertical);
        blender.AddTile(r1, width, r1H, new TileOverlap(0, 16));
        blender.AddTile(r2, width, r2H, new TileOverlap(16, 0));

        byte[] result = blender.GetResult();
        Assert.Equal(src, result);
    }

    [Fact]
    public void TileBlender_2DGrid_SeamlessReconstruction()
    {
        int width = 120;
        int height = 140;
        int maxTileSizeX = 70;
        int maxTileSizeY = 80;
        int overlap = 16;
        byte[] src = CreateTestImage(width, height);

        int tileCountX = (int)Math.Ceiling((double)width / maxTileSizeX); // 2
        int tileCountY = (int)Math.Ceiling((double)height / maxTileSizeY); // 2
        int tileSizeX = (int)Math.Ceiling((double)width / tileCountX); // 60
        int tileSizeY = (int)Math.Ceiling((double)height / tileCountY); // 70

        var imageBlender = new TileBlender(width, height, 3, BlendDirection.Vertical);

        for (int y = 0; y < tileCountY; y++)
        {
            int tileY = y * tileSizeY;
            int tileH = Math.Min(tileSizeY, height - tileY);
            int padTop = Math.Min(tileY, overlap);
            int padBottom = Math.Min(height - (tileY + tileH), overlap);
            int paddedH = tileH + padTop + padBottom;

            var rowBlender = new TileBlender(width, paddedH, 3, BlendDirection.Horizontal);
            var rowOverlap = new TileOverlap(padTop, padBottom);

            for (int x = 0; x < tileCountX; x++)
            {
                int tileX = x * tileSizeX;
                int tileW = Math.Min(tileSizeX, width - tileX);
                int padLeft = Math.Min(tileX, overlap);
                int padRight = Math.Min(width - (tileX + tileW), overlap);
                int paddedW = tileW + padLeft + padRight;

                int paddedX = tileX - padLeft;
                int paddedY = tileY - padTop;

                byte[] crop = OnnxTiler.ExtractCrop(src, width, paddedX, paddedY, paddedW, paddedH);
                var tileOverlap = new TileOverlap(padLeft, padRight);
                rowBlender.AddTile(crop, paddedW, paddedH, tileOverlap);
            }

            imageBlender.AddTile(rowBlender.GetResult(), width, paddedH, rowOverlap);
        }

        byte[] result = imageBlender.GetResult();
        Assert.Equal(src, result);
    }

    private static byte[] CreateTestImage(int width, int height)
    {
        byte[] img = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 3;
                img[idx] = (byte)((x * 3 + y * 7) % 256);
                img[idx + 1] = (byte)((x * 11 + y * 5) % 256);
                img[idx + 2] = (byte)((x * 13 + y * 17) % 256);
            }
        }

        return img;
    }

    /// <summary>
    /// Writes a tiny upscaler whose single input and output carry names other than the
    /// <c>input</c>/<c>output</c> the tiler requires, so the "model is unusable" path can be driven
    /// through a real ONNX Runtime session rather than a hand-built value collection.
    /// <para>
    /// ONNX stores tensor names as length-prefixed UTF-8, so renaming a generated model in place only
    /// works when the replacement is the same length: <c>"input"</c> becomes <c>"image"</c> and
    /// <c>"output"</c> becomes <c>"result"</c>, both byte-for-byte equal in size.
    /// </para>
    /// </summary>
    private static string WriteModelWithForeignTensorNames(string directory, bool fp16 = false)
    {
        string path = TinyOnnxModel.WriteNearestUpscaler(directory, scale: 2, fp16: fp16);
        byte[] bytes = File.ReadAllBytes(path);
        RenameAscii(bytes, "input", "image");
        RenameAscii(bytes, "output", "result");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void RenameAscii(byte[] bytes, string from, string to)
    {
        ReadOnlySpan<byte> source = System.Text.Encoding.ASCII.GetBytes(from);
        ReadOnlySpan<byte> target = System.Text.Encoding.ASCII.GetBytes(to);
        if (source.Length != target.Length)
        {
            throw new ArgumentException($"in-place rename of '{from}' to '{to}' changes length");
        }

        Span<byte> remaining = bytes;
        int at;
        while ((at = remaining.IndexOf(source)) >= 0)
        {
            target.CopyTo(remaining[at..]);
            remaining = remaining[(at + source.Length)..];
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void UpscaleRgb_ProducesConsistentOutputBetweenSinglePassAndTiled()
    {
        // The overlap blending is the part of the tiler with no analytic test: whether a seam is
        // visible is a property of the seam itself. A real model used to be needed for it, which
        // made it the one test in this file that could not run anywhere but a developer machine —
        // a nearest-neighbour upscaler reproduces the blending identically, since the blending is
        // about geometry rather than weights.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(CreateDir(), scale: 2);
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath);
        int w = 64;
        int h = 64;
        int scale = 2;
        byte[] input = CreateTestImage(w, h);

        // 1. Single pass (whole image)
        byte[] singlePass = OnnxTiler.UpscaleRgb(
            input,
            w,
            h,
            scale,
            session,
            tileSize: 0,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // 2. Tiled pass (force 32px max tile size -> 2x2 tiles with half-sine overlap blending)
        byte[] tiledPass = OnnxTiler.UpscaleRgb(
            input,
            w,
            h,
            scale,
            session,
            tileSize: 32,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(w * scale * h * scale * 3, singlePass.Length);
        Assert.Equal(w * scale * h * scale * 3, tiledPass.Length);

        // Check that the two results are very close (half-sine blend minimizes seam divergence)
        double totalDiff = 0;
        for (int i = 0; i < singlePass.Length; i++)
        {
            totalDiff += Math.Abs(singlePass[i] - tiledPass[i]);
        }
        double avgDiff = totalDiff / singlePass.Length;
        Assert.True(avgDiff < 5.0, $"Average pixel difference should be small, but was {avgDiff}");
    }

    [Fact]
    public void UpscaleTile_ModelWhoseInputIsNotNamedInput_ThrowsClearError()
    {
        // Candidate selection can point at a model that names its single input something else. A bare
        // InputMetadata["input"] threw a KeyNotFoundException that mentioned neither the model nor
        // the names it does use, so the caller could not tell "this model is unusable" from a genuine
        // tiler bug. The message now names the offending tensor and what the model actually has.
        string modelPath = WriteModelWithForeignTensorNames(CreateDir());
        using var session = new InferenceSession(modelPath);
        Assert.True(
            session.InputMetadata.ContainsKey("image"),
            "fixture: the model input should have been renamed off 'input'"
        );

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxTiler.UpscaleTile(
                CreateTestImage(32, 32),
                32,
                32,
                2,
                session,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("no input named", ex.Message);
        Assert.Contains("'image'", ex.Message);
    }

    [Fact]
    public void DecodeOutputTensor_ModelWhoseOutputIsNotNamedOutput_ThrowsClearError()
    {
        // The same for the output lookup: First(o => o.Name == "output") raised an empty
        // InvalidOperationException ("Sequence contains no matching element") that said nothing about
        // the model, so it is now resolved to a message naming the tensor the model really produced.
        string modelPath = WriteModelWithForeignTensorNames(CreateDir());
        using var session = new InferenceSession(modelPath);
        Assert.True(
            session.OutputMetadata.ContainsKey("result"),
            "fixture: the model output should have been renamed off 'output'"
        );

        var input = new DenseTensor<float>(
            Enumerable.Repeat(1f, 3 * 2 * 2).ToArray(),
            [1, 3, 2, 2]
        );
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([
            NamedOnnxValue.CreateFromTensor("image", input),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxTiler.DecodeOutputTensor(outputs, 2, 2, 2, 2, 2, CancellationToken.None)
        );

        Assert.Contains("no output named", ex.Message);
        Assert.Contains("'result'", ex.Message);
    }

    [Theory]
    [InlineData("4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx", ModelArchitecture.Esrgan)]
    [InlineData("2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx", ModelArchitecture.Esrgan)]
    [InlineData("2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx", ModelArchitecture.Span)]
    [InlineData("4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx", ModelArchitecture.FdatM)]
    [InlineData("4x_IllustrationJaNai_V2standard_FDAT_XL_18k.onnx", ModelArchitecture.FdatXl)]
    [InlineData("4x_IllustrationJaNai_V1_DAT2_190k.onnx", ModelArchitecture.Dat2)]
    [InlineData("4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx", ModelArchitecture.HatL)]
    [InlineData("random_custom_model.onnx", ModelArchitecture.Unknown)]
    public void DetectArchitecture_IdentifiesArchitecturesCorrectly(
        string modelName,
        ModelArchitecture expected
    )
    {
        Assert.Equal(expected, OnnxTiler.DetectArchitecture(modelName));
    }

    [Fact]
    public void EstimatePeakMemoryBytes_CalculatesRealisticRequirements()
    {
        // 1600x2400 on 4x ESRGAN (FP32): the whole page at once is genuinely enormous — ~111 GiB at
        // the measured fp32 coefficient (463 live channels at output resolution)
        long esrgan4xPeak = OnnxTiler.EstimatePeakMemoryBytes(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            isFp16: false
        );
        Assert.InRange(esrgan4xPeak, 100L * 1024 * 1024 * 1024, 130L * 1024 * 1024 * 1024);

        // 1600x2400 on 2x SPAN (FP16): ~10 GiB of activations at 315 live channels
        long span2xPeak = OnnxTiler.EstimatePeakMemoryBytes(
            1600,
            2400,
            2,
            "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
            isFp16: true
        );
        Assert.InRange(span2xPeak, 8L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void EstimatePeakMemoryBytes_ScaleQuadruplesFootprint()
    {
        long at2x = OnnxTiler.EstimatePeakMemoryBytes(
            512,
            512,
            2,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            isFp16: true
        );
        long at4x = OnnxTiler.EstimatePeakMemoryBytes(
            512,
            512,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            isFp16: true
        );
        // scale^2 scaling: the activation term quadruples, the weight term does not
        Assert.True(at4x > 3 * at2x);
    }

    [Fact]
    public void EstimateActivationBytes_ScalesQuadraticallyWithTileSize()
    {
        long small = OnnxTiler.EstimateActivationBytes(
            256 * 256,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            isFp16: true
        );
        long large = OnnxTiler.EstimateActivationBytes(
            512 * 512,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            isFp16: true
        );
        Assert.Equal(4 * small, large);
    }

    [Fact]
    public void PlanTileSplit_WholeImageFits_IsASingleTile()
    {
        // SPAN on a small page: the full page fits well inside a 12 GB budget -> single pass
        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            640,
            960,
            2,
            "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
            memoryBudgetBytes: 12L * 1024 * 1024 * 1024,
            isFp16: true
        );
        Assert.Equal(new OnnxTiler.TileSplit(1, 1, 640, 960), split);
    }

    [Fact]
    public void PlanTileSplit_Esrgan4x_DerivesGridFromBudget()
    {
        // 5 GiB budget on a 1600x2400 4x ESRGAN page in FP32: 320x343 tiles in a 5x7 grid. The
        // smaller side is 320 px, the largest any grid can give at this budget once the session
        // reservation comes off it, and 5x7 is the cheapest of the grids that tie there.
        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 5L * 1024 * 1024 * 1024,
            isFp16: false
        );

        Assert.Equal(5, split.Columns);
        Assert.Equal(7, split.Rows);
        Assert.Equal(384, OnnxTiler.UniformTileTensorShape(split).Width);
    }

    [Fact]
    public void PlanTileSplit_Esrgan2x_AllowsLargerTilesThan4x()
    {
        // Same budget, half the scale -> four times the pixels fit -> much larger tiles
        OnnxTiler.TileSplit split2x = OnnxTiler.PlanTileSplit(
            1600,
            2400,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            memoryBudgetBytes: 5L * 1024 * 1024 * 1024,
            isFp16: false
        );
        OnnxTiler.TileSplit split4x = OnnxTiler.PlanTileSplit(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 5L * 1024 * 1024 * 1024,
            isFp16: false
        );

        Assert.True(split2x.TileWidth > split4x.TileWidth);
        Assert.True(split2x.TotalPaddedPixels <= split4x.TotalPaddedPixels);

        // What has to be round is the tensor the tiles are inferred at.
        Assert.Equal(0, OnnxTiler.UniformTileTensorShape(split2x).Width % 64);
        Assert.Equal(0, OnnxTiler.UniformTileTensorShape(split2x).Height % 64);
    }

    [Fact]
    public void PlanTileSplit_SmallImage_IsASingleTile()
    {
        // Small 64x64 image fits in memory under any budget -> single pass
        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            64,
            64,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 4L * 1024 * 1024 * 1024,
            isFp16: false
        );
        Assert.Equal(new OnnxTiler.TileSplit(1, 1, 64, 64), split);
    }

    [Fact]
    public void PlanTileSplit_TightBudget_SplitsAtTheQualityFloor()
    {
        // Even under a very low budget (e.g. 500 MB) the fallback the quality floor produces is
        // returned: TileSplit.For at MinimumTileSize. It shrinks its tiles to cover the page exactly,
        // so they can land below the floor (229x240 here), and the engine halves them again if the
        // driver refuses the allocation. The enumeration itself never offers a sub-floor candidate.
        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 500L * 1024 * 1024,
            isFp16: false
        );

        Assert.Equal(OnnxTiler.TileSplit.For(1600, 2400, OnnxTiler.MinimumTileSize), split);
    }

    [Fact]
    public void PlanTileSplit_MangaJaNai_FP16_UsesLessPeakMemoryAndExpandsGrid()
    {
        long actFp32 = OnnxTiler.EstimateActivationBytes(
            1125 * 1600,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            isFp16: false
        );
        long actFp16 = OnnxTiler.EstimateActivationBytes(
            1125 * 1600,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            isFp16: true
        );
        // The fp16 working set is smaller, but by *less* than the element-size ratio: the fp16
        // export casts to fp32 around the upsampler, so its peak is already partly fp32-sized
        // (measured 0.65x the fp32 footprint here, where halving would be 0.50x). The old table
        // assumed an exact halving and therefore under-estimated the fp32 transformer exports.
        Assert.True(actFp16 < actFp32, $"fp16 should cost less: {actFp16} vs {actFp32}");
        Assert.True(
            actFp16 > actFp32 / 2,
            $"fp16 should cost more than half the fp32 set: {actFp16} vs {actFp32 / 2}"
        );

        // Under a constrained 3 GiB budget, FP16 yields strictly larger tiles than FP32
        OnnxTiler.TileSplit splitFp32 = OnnxTiler.PlanTileSplit(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 3L * 1024 * 1024 * 1024,
            isFp16: false
        );
        OnnxTiler.TileSplit splitFp16 = OnnxTiler.PlanTileSplit(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 3L * 1024 * 1024 * 1024,
            isFp16: true
        );

        Assert.True(splitFp16.TileWidth > splitFp32.TileWidth);
        Assert.True(splitFp16.TotalPaddedPixels < splitFp32.TotalPaddedPixels);
    }

    [Fact]
    public void PlanTileSplit_PlannedGridFitsInsideBudget()
    {
        // The core invariant: whatever grid the planner hands back, running one tile of it (plus
        // overlap and alignment) must stay inside the budget.
        (string Model, int Scale)[] cases = new (string Model, int Scale)[]
        {
            ("4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx", 4),
            ("2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx", 2),
            ("2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx", 2),
            ("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx", 4),
            ("4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16.onnx", 4),
            ("4x_IllustrationJaNai_V1_DAT2_190k.onnx", 4),
            ("4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx", 4),
        };

        long[] budgets = new long[] { 512, 1024, 2048, 4096, 8192, 15360 };
        foreach (long budgetMb in budgets)
        {
            long budget = budgetMb * 1024L * 1024L;
            foreach (var (model, scale) in cases)
            {
                OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
                    1125,
                    1600,
                    scale,
                    model,
                    memoryBudgetBytes: budget,
                    isFp16: true
                );
                long tileFootprint =
                    OnnxTiler.EstimateModelBytes(model)
                    + OnnxTiler.EstimateActivationBytes(
                        (int)split.PaddedTilePixels,
                        scale,
                        model,
                        isFp16: true
                    )
                    // The session reservation is device memory the tile holds too, and the planner
                    // now subtracts it from the budget. Asserting without it here would re-implement
                    // the very omission being tested for, and pass either way.
                    + OnnxTiler.SessionReservationBytes;

                // A budget too small for even the quality floor is the one case where the grid may
                // exceed the budget: the floor protects output quality and the engine halves the
                // tiles again on allocation failure.
                if (
                    split.TileWidth > OnnxTiler.MinimumTileSize
                    || split.TileHeight > OnnxTiler.MinimumTileSize
                )
                {
                    Assert.True(
                        tileFootprint <= budget,
                        $"{model} @ {budgetMb} MB budget planned {split.Columns}x{split.Rows} of {split.TileWidth}x{split.TileHeight} needing {tileFootprint / 1048576} MB"
                    );
                }
            }
        }
    }

    [Fact]
    public void PlanTileSplit_NeverPlansASubFloorGridExceptTheEmergencyFallback()
    {
        // Every planned grid keeps both tile sides at or above MinimumTileSize. The one exception is
        // the fallback returned when no affordable grid exists: it is TileSplit.For at the floor,
        // which shrinks its tiles to cover the page exactly and can therefore land below it, and the
        // engine halves it again when the driver refuses the allocation. Before the enumeration used
        // floor division it could select a sub-floor grid instead (800x5685 planned 4x20 of
        // 200x285), which is the defect this pins.
        (int Width, int Height)[] pages =
        {
            (1125, 1600),
            (1600, 2400),
            (800, 5685),
            (1000, 1000),
            (300, 300),
        };
        long[] budgetsMb = { 512, 1024, 2048, 2176, 3072, 4096, 8192, 16384 };

        foreach ((int width, int height) in pages)
        {
            OnnxTiler.TileSplit fallback = OnnxTiler.TileSplit.For(
                width,
                height,
                OnnxTiler.MinimumTileSize
            );
            foreach (long budgetMb in budgetsMb)
            {
                OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
                    width,
                    height,
                    4,
                    "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                    budgetMb * 1024L * 1024L,
                    isFp16: true
                );

                if (split == fallback)
                {
                    continue;
                }

                Assert.True(
                    split.TileWidth >= OnnxTiler.MinimumTileSize
                        && split.TileHeight >= OnnxTiler.MinimumTileSize,
                    $"{width}x{height} @ {budgetMb} MB planned {split.Columns}x{split.Rows} of "
                        + $"{split.TileWidth}x{split.TileHeight}, below the "
                        + $"{OnnxTiler.MinimumTileSize} px floor"
                );
            }
        }
    }

    [Fact]
    public void PlanTileSplit_MoreBudgetNeverDoesMoreWork()
    {
        long previous = long.MaxValue;
        foreach (long budgetMb in new long[] { 512, 1024, 2048, 4096, 6144, 8192, 12288, 16384 })
        {
            OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
                1125,
                1600,
                4,
                "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                memoryBudgetBytes: budgetMb * 1024L * 1024L,
                isFp16: true
            );
            Assert.True(
                split.TotalPaddedPixels <= previous,
                $"work grew from {previous} to {split.TotalPaddedPixels} pixels at {budgetMb} MB"
            );
            previous = split.TotalPaddedPixels;
        }

        // 1125x1600 at 4x fp16 needs ~34 GB of activation budget, so a 16 GB budget cannot keep the
        // page whole. The cheapest grid that fits is one column of three 1125x534 tiles: the page is
        // taller than wide, so the budget is best spent on rows. A square-derived 800 px tile side
        // would have split it 2x2 instead, for 1.4% more work and one more inference.
        OnnxTiler.TileSplit best = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 16384L * 1024L * 1024L,
            isFp16: true
        );
        Assert.Equal(1, best.Columns);
        Assert.Equal(3, best.Rows);
    }

    [Fact]
    public void PlanTileSplit_HasNoScaleAwareUpperCap()
    {
        // The old model clamped 4x models to 512 px and 2x models to 1024 px regardless of memory.
        // Deriving from the budget alone, a 32 GiB budget lets a 4x model plan 1125 px tiles and a
        // 2x model skip tiling entirely.
        long hugeBudget = 32L * 1024 * 1024 * 1024;

        OnnxTiler.TileSplit split4x = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: hugeBudget,
            isFp16: true
        );
        Assert.True(split4x.TileWidth > 1024, $"expected large tiles, got {split4x.TileWidth}");

        OnnxTiler.TileSplit split2x = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            memoryBudgetBytes: hugeBudget,
            isFp16: true
        );
        Assert.Equal(new OnnxTiler.TileSplit(1, 1, 1125, 1600), split2x);
    }

    [Fact]
    public void PlanTileSplit_ElongatedImage_DoesNotOomOrAssumeZeroWhenPaddingIsSignificant()
    {
        // 8000x200 has 1.6 MP raw, but 4x ESRGAN needs ~100 GB of activations for it in one pass, so
        // it must tile instead of running it whole.
        long budget = 2L * 1024 * 1024 * 1024; // 2 GB budget
        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            8000,
            200,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: budget,
            isFp16: true
        );

        Assert.True(
            split.Columns > 1 || split.Rows > 1,
            "a 1.6 MP 4x ESRGAN page cannot be one pass"
        );

        // The strip is followed along its long axis: one row, many columns.
        Assert.Equal(1, split.Rows);
        Assert.True(split.Columns > 1, "the long axis has to be split");

        // 28 columns is the cheapest grid whose padded tile still fits the budget. The padding is
        // what decides it: 28 columns give a 286 px tile whose padded width is 320, while 27 give a
        // 297 px tile that rounds up to 384 and costs more. A planner that ignored the overlap would
        // pick a grid it cannot honour on a strip this thin.
        Assert.Equal(28, split.Columns);
        Assert.True(
            OnnxTiler.PredictTilePeakBytes(
                split,
                4,
                "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
                0,
                isFp16: true
            ) <= budget,
            "the planned grid must fit the budget, padding included"
        );
    }

    [Fact]
    public void PrepareInputTensor_PaddingLongerThanTheTile_ReflectsInsteadOfRepeatingTheEdge()
    {
        // A small tile padded to a much larger uniform tensor needs more padding than the tile is
        // wide. A single reflection only covers padding shorter than the tile and used to clamp to the
        // edge past that, so the far part of the tensor became a constant run of the edge pixel and the
        // model had no structure to work from there. The reflection now repeats (a triangular wave),
        // so the content keeps changing across the whole padded region.
        const int tile = 4;
        byte[] bytes = new byte[tile * tile * 3];
        for (int y = 0; y < tile; y++)
        {
            for (int x = 0; x < tile; x++)
            {
                bytes[(y * tile + x) * 3] = (byte)(x * 40 + 10); // distinct per column
            }
        }

        var (inVal, paddedW, paddedH) = OnnxTiler.PrepareInputTensor(
            bytes,
            tile,
            tile,
            128,
            128,
            typeof(Float16),
            TestContext.Current.CancellationToken
        );

        // NamedOnnxValue is not disposable; the tensor it wraps is managed.
        var tensor = inVal.AsTensor<Float16>();
        Assert.Equal(128, paddedW);
        Assert.Equal(128, paddedH);

        // Past the tile (index 4), the old single reflection went 3,2,1,0 and then clamped at 0
        // forever; the wave keeps moving, so neighbouring padded columns differ.
        Assert.NotEqual(tensor[0, 0, 0, 8], tensor[0, 0, 0, 9]);
        Assert.NotEqual(tensor[0, 0, 0, 10], tensor[0, 0, 0, 11]);
    }

    [Fact]
    public void UniformTileTensorShape_FollowsThePageNotTheBudget()
    {
        // A 1536 px tile bound on an 800 px wide, 5685 px tall page splits 1x4 into 800x1422 tiles.
        // The uniform tensor shape has to be 832x1472 -- the tiles the page actually has -- and not
        // 1600x1600, which is what the budget's tile side would demand. Inferring at the budget's
        // shape computes every tile at twice the pixels the page contains, for identical output.
        OnnxTiler.TileSplit split = OnnxTiler.TileSplit.For(800, 5685, 1536);
        (int width, int height) = OnnxTiler.UniformTileTensorShape(split);

        Assert.Equal((832, 1472), (width, height));
    }

    [Fact]
    public void UniformTileTensorShape_SinglePassCoversThePage()
    {
        // Whole page as one inference: the shape is the page plus the edge overlap it is inferred
        // with, rounded up to the tensor alignment -- no more.
        (int width, int height) = OnnxTiler.UniformTileTensorShape(
            OnnxTiler.TileSplit.For(1125, 1600, 0)
        );

        Assert.Equal((1216, 1664), (width, height));
    }

    [Fact]
    public void UniformTileTensorShape_NeverDropsBelowTheAlignment()
    {
        // A degenerate split still yields a multiple of the alignment, because the driver is spared
        // a one-off shape.
        (int width, int height) = OnnxTiler.UniformTileTensorShape(
            new OnnxTiler.TileSplit(1, 1, 1, 1)
        );

        Assert.Equal(0, width % OnnxTiler.TileAlignment);
        Assert.Equal(0, height % OnnxTiler.TileAlignment);
    }

    [Fact]
    public void TileSplit_For_CoversThePageExactlyOnce()
    {
        foreach (int tile in new[] { 0, 64, 256, 377, 512, 1024, 4096 })
        {
            foreach ((int width, int height) in new[] { (1125, 1600), (800, 5685), (8000, 200) })
            {
                OnnxTiler.TileSplit split = OnnxTiler.TileSplit.For(width, height, tile);

                Assert.True(split.Columns >= 1 && split.Rows >= 1, $"no tiles for {tile}");
                Assert.True(
                    split.Columns * split.TileWidth >= width,
                    $"{width}x{height} @ {tile}: {split.Columns}x{split.TileWidth} columns do not cover"
                );
                Assert.True(
                    split.Rows * split.TileHeight >= height,
                    $"{width}x{height} @ {tile}: {split.Rows}x{split.TileHeight} rows do not cover"
                );
                Assert.True(
                    (split.Columns - 1) * split.TileWidth < width,
                    $"{width}x{height} @ {tile}: {split.Columns} columns is more than needed"
                );
                Assert.True(
                    (split.Rows - 1) * split.TileHeight < height,
                    $"{width}x{height} @ {tile}: {split.Rows} rows is more than needed"
                );
            }
        }
    }

    [Theory]
    // A square split: both axes double and the tile side halves exactly.
    [InlineData(1024, 1024, 2, 2, 512, 512)]
    // The larger side is 563, so TileSplit.For is asked for 281 (integer floor, not 282).
    [InlineData(1125, 1600, 2, 3, 563, 534)]
    [InlineData(1600, 2400, 6, 9, 267, 267)]
    [InlineData(8000, 200, 32, 1, 250, 200)]
    // A single pass halves into a 2x2 grid rather than staying whole.
    [InlineData(300, 300, 1, 1, 300, 300)]
    public void HalveSplit_HalvesTheLargestSideAndStillCoversThePage(
        int width,
        int height,
        int columns,
        int rows,
        int tileWidth,
        int tileHeight
    )
    {
        var split = new OnnxTiler.TileSplit(columns, rows, tileWidth, tileHeight);

        OnnxTiler.TileSplit halved = OnnxTiler.HalveSplit(width, height, split);

        int previousLargestSide = Math.Max(split.TileWidth, split.TileHeight);
        int halvedLargestSide = Math.Max(halved.TileWidth, halved.TileHeight);
        Assert.True(
            halvedLargestSide <= previousLargestSide / 2,
            $"{previousLargestSide} px side halved to {halvedLargestSide} px"
        );

        // Never a zero-sized tile: the division floors and TileSplit.For clamps to at least one.
        Assert.True(halved.TileWidth >= 1 && halved.TileHeight >= 1);

        // The new grid still covers the page exactly once, the contract TileSplit.For upholds.
        Assert.True(halved.Columns * halved.TileWidth >= width);
        Assert.True((halved.Columns - 1) * halved.TileWidth < width);
        Assert.True(halved.Rows * halved.TileHeight >= height);
        Assert.True((halved.Rows - 1) * halved.TileHeight < height);
    }

    [Fact]
    public void HalveSplit_RepeatedlyHalvesUntilTheEngineStopsRetrying()
    {
        // The engine halves while a tile side is above 128 px, so the loop has to keep shrinking
        // rather than settling or oscillating before it gets there.
        var split = new OnnxTiler.TileSplit(1, 1, 1125, 1600);
        int previous = Math.Max(split.TileWidth, split.TileHeight);
        while (previous > 128)
        {
            split = OnnxTiler.HalveSplit(1125, 1600, split);
            int current = Math.Max(split.TileWidth, split.TileHeight);
            Assert.True(current < previous, $"{previous} px did not shrink");
            Assert.True(current >= 1);
            previous = current;
        }

        Assert.True(previous <= 128);
    }

    [Fact]
    public void HalveSplit_WhenTheLargestSideIsOne_FallsBackToTheWholePageRatherThanZero()
    {
        // Math.Max(side) / 2 floors to zero, and TileSplit.For reads a non-positive bound as "one
        // tile", so the degenerate one-pixel split comes back as the whole page instead of a
        // zero-width tile. The retry loop stops far above this, but the helper must not divide a tile
        // out of existence if it is ever reached.
        OnnxTiler.TileSplit halved = OnnxTiler.HalveSplit(
            1125,
            1600,
            new OnnxTiler.TileSplit(1, 1, 1, 1)
        );

        Assert.Equal(new OnnxTiler.TileSplit(1, 1, 1125, 1600), halved);
    }

    [Fact]
    public void IsMemoryException_AcceptsOutOfMemoryException()
    {
        Assert.True(
            OnnxTiler.IsMemoryException(new OutOfMemoryException("device memory exhausted"))
        );
    }

    [Theory]
    // One representative per recognised provider/allocator substring; the check is case-insensitive.
    [InlineData("Could not allocate memory")]
    [InlineData("CUDA failure: out of memory")]
    [InlineData("cudaMalloc failed")]
    [InlineData("hipMalloc returned an error")]
    [InlineData("vkQueueSubmit failed with VK_ERROR_OUT_OF_DEVICE_MEMORY")]
    [InlineData("VK_ERROR_DEVICE_LOST")]
    [InlineData("command submission failed")]
    [InlineData("raw_hash_map allocation failed")]
    [InlineData("DXGI_ERROR_DEVICE_REMOVED")]
    [InlineData("CL_MEM_OBJECT_ALLOCATION_FAILURE")]
    public void IsMemoryException_AcceptsRecognisedOnnxRuntimeMessages(string message)
    {
        Assert.True(OnnxTiler.IsMemoryException(OnnxRuntimeExceptionWithMessage(message)));
    }

    [Fact]
    public void IsMemoryException_RejectsUnrelatedOnnxRuntimeMessages()
    {
        Assert.False(
            OnnxTiler.IsMemoryException(
                OnnxRuntimeExceptionWithMessage("Input name: 'image' is not in the metadata")
            )
        );
    }

    [Fact]
    public void IsMemoryException_RejectsUnrelatedExceptions()
    {
        Assert.False(
            OnnxTiler.IsMemoryException(new InvalidOperationException("not a memory error"))
        );
        Assert.False(OnnxTiler.IsMemoryException(new IOException("disk full")));
    }

    [Fact]
    public void IsMemoryException_LooksThroughWrappingInnerExceptions()
    {
        // A plain wrapper around either kind is what the call sites actually see, since ONNX Runtime
        // and the engine both rethrow with context.
        Assert.True(
            OnnxTiler.IsMemoryException(
                new InvalidOperationException(
                    "tile failed",
                    OnnxRuntimeExceptionWithMessage("cudaMalloc failed")
                )
            )
        );
        Assert.True(
            OnnxTiler.IsMemoryException(
                new Exception("outer", new Exception("inner", new OutOfMemoryException()))
            )
        );
        Assert.False(
            OnnxTiler.IsMemoryException(
                new InvalidOperationException("outer", new InvalidOperationException("inner"))
            )
        );
    }

    /// <summary>
    /// Builds an <see cref="OnnxRuntimeException"/> carrying a chosen message. Its only constructor
    /// and the <c>ErrorCode</c> it takes are internal to Microsoft.ML.OnnxRuntime, so binding them is
    /// the only way to put a specific message — the allocator substrings the classifier keys on — in
    /// front of it without provoking a real out-of-memory on a device.
    /// </summary>
    private static OnnxRuntimeException OnnxRuntimeExceptionWithMessage(string message)
    {
        Type errorCode =
            typeof(OnnxRuntimeException).Assembly.GetType("Microsoft.ML.OnnxRuntime.ErrorCode")
            ?? throw new InvalidOperationException("ErrorCode type not found");
        ConstructorInfo constructor =
            typeof(OnnxRuntimeException).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [errorCode, typeof(string)],
                modifiers: null
            ) ?? throw new InvalidOperationException("OnnxRuntimeException constructor not found");
        object code = Enum.Parse(errorCode, "RuntimeException");
        return (OnnxRuntimeException)constructor.Invoke([code, message]);
    }

    [Fact]
    public void PlanTileSplit_GivesTheSmallerSideAllTheContextTheBudgetAllows()
    {
        // The quality rule: a tile side below QualityTilePixels is worse, and above it there is
        // nothing left to look at. So the planner must maximise the smaller side up to that target,
        // and among the grids that tie on it take the cheapest. Brute-forcing every grid proves no
        // better one was left on the table — both that no feasible grid has the smaller side longer,
        // and that none of the ties is cheaper.
        (string Model, int Scale)[] cases = new (string Model, int Scale)[]
        {
            ("4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx", 4),
            ("2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx", 2),
            ("4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx", 4),
        };

        foreach (long budgetMb in new long[] { 1024, 2048, 4096, 8192, 12288, 16384 })
        {
            long budget = budgetMb * 1024L * 1024L;
            foreach (var (model, scale) in cases)
            {
                OnnxTiler.TileSplit chosen = OnnxTiler.PlanTileSplit(
                    1125,
                    1600,
                    scale,
                    model,
                    budget,
                    isFp16: true
                );
                if (chosen.Columns == 1 && chosen.Rows == 1)
                {
                    continue; // the whole page in one pass, decided before any grid is considered
                }

                int chosenContext = Math.Min(
                    Math.Min(chosen.TileWidth, chosen.TileHeight),
                    OnnxTiler.QualityTilePixels
                );
                double bytesPerPixel = OnnxTiler
                    .ActivationCoefficients.For(model)
                    .BytesPerPixel(scale, isFp16: true);

                // The same bound the planner enumerates: floor division, so no candidate falls below
                // the MinimumTileSize floor. A ceil bound would consider 1125 -> 5 columns -> 225.
                int maxColumns = Math.Max(1, 1125 / OnnxTiler.MinimumTileSize);
                int maxRows = Math.Max(1, 1600 / OnnxTiler.MinimumTileSize);
                for (int columns = 1; columns <= maxColumns; columns++)
                {
                    for (int rows = 1; rows <= maxRows; rows++)
                    {
                        var candidate = new OnnxTiler.TileSplit(
                            columns,
                            rows,
                            (1125 + columns - 1) / columns,
                            (1600 + rows - 1) / rows
                        );
                        if (
                            OnnxTiler.EstimateModelBytes(model)
                                + (long)(bytesPerPixel * candidate.PaddedTilePixels)
                            > budget - OnnxTiler.SessionReservationBytes
                        )
                        {
                            continue; // not affordable at this budget
                        }

                        int context = Math.Min(
                            Math.Min(candidate.TileWidth, candidate.TileHeight),
                            OnnxTiler.QualityTilePixels
                        );

                        Assert.True(
                            context <= chosenContext,
                            $"{model} @ {budgetMb} MB: grid {columns}x{rows} gives the smaller side "
                                + $"{context} px of context, more than the chosen "
                                + $"{chosen.Columns}x{chosen.Rows} at {chosenContext}"
                        );

                        if (context < chosenContext)
                        {
                            continue; // worse context, so its cost is irrelevant
                        }

                        Assert.True(
                            candidate.TotalPaddedPixels >= chosen.TotalPaddedPixels,
                            $"{model} @ {budgetMb} MB: grid {columns}x{rows} matches the chosen "
                                + $"{chosen.Columns}x{chosen.Rows} on context and is cheaper"
                        );
                    }
                }
            }
        }
    }

    [Theory]
    // A budget that can afford a side at or past the quality target must not settle for less. The
    // fp32 transformer model is the case this came from: it used to plan 282x800 tiles — the smaller
    // side was 282, well under the target — while a 563-wide grid was affordable all along. (The
    // smaller side is 400 rather than 512 here because the fp32 coefficient is now the measured 590,
    // not the fp16 460 that under-estimated this export.)
    [InlineData(13429, 400)]
    // Below the target the smaller side is whatever the budget allows, and it must be the largest
    // available rather than the one that minimises work.
    [InlineData(7111, 320)]
    public void PlanTileSplit_ReachesForTheQualityTargetBeforeSpendingOnSize(
        int budgetMb,
        int expectedSmallerSide
    )
    {
        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx",
            budgetMb * 1024L * 1024L,
            isFp16: false
        );

        Assert.Equal(expectedSmallerSide, Math.Min(split.TileWidth, split.TileHeight));

        // Whatever the budget allows, the smaller side must never be the quality floor's size.
        Assert.True(Math.Min(split.TileWidth, split.TileHeight) >= OnnxTiler.MinimumTileSize);
    }

    [Fact]
    public void PlanTileSplit_AboveTheQualityTarget_SpendsTheHeadroomOnLessWork()
    {
        // Once the smaller side reaches the target the size has bought all the quality it can, so
        // the remaining headroom goes to the cheapest grid rather than to an even larger tile. Two
        // columns of 563 px and one column of 1125 px both give the smaller side the full target;
        // the cheaper one is the single column, and it is the one to take.
        long generous = 32L * 1024 * 1024 * 1024;

        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
            generous,
            isFp16: true
        );

        Assert.Equal(1, split.Columns);
        Assert.True(split.TileHeight >= OnnxTiler.QualityTilePixels);
    }

    [Fact]
    public void PlanTileSplit_DoesNotRevisitTheSinglePassDecision()
    {
        // A page that fits the budget but not comfortably enough for a single pass must not get one
        // back from the grid loop: one tile of the whole page is the cheapest grid there is, and
        // taking it would undo the safety fraction the single-pass decision applied.
        long budget = 2L * 1024 * 1024 * 1024;
        string model = "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx";
        double bytesPerPixel = OnnxTiler
            .ActivationCoefficients.For(model)
            .BytesPerPixel(4, isFp16: false);
        long wholePage = OnnxTiler.EstimateModelBytes(model) + (long)(bytesPerPixel * 1125 * 1600);

        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            isFp16: false
        );

        Assert.False(
            split.Columns == 1 && split.Rows == 1,
            "a whole-page tile came back from the grid loop"
        );
        Assert.True(
            wholePage > budget * OnnxTiler.SinglePassSafetyFraction,
            "precondition: the page should be too large for a comfortable single pass"
        );
    }

    [Fact]
    public void CalculateVramBudget_HeadlessNas8Gb_UtilizesNearlyFullVram()
    {
        // 8 GB card on a dedicated NAS with minimal driver usage (50 MB used)
        long total = 8L * 1024 * 1024 * 1024;
        long used = 50L * 1024 * 1024;
        long budget = OnnxTiler.CalculateVramBudget(total, used);

        // Should utilize ~7.7 GB of the 8 GB (only a 256 MB buffer subtracted)
        Assert.InRange(budget, (long)(7.6 * 1024 * 1024 * 1024), total);
    }

    [Fact]
    public void CalculateVramBudget_HeadlessNas12Gb_UtilizesNearlyFullVram()
    {
        // 12 GB card on a dedicated NAS with minimal driver usage (50 MB used)
        long total = 12L * 1024 * 1024 * 1024;
        long used = 50L * 1024 * 1024;
        long budget = OnnxTiler.CalculateVramBudget(total, used);

        // Should utilize ~11.7 GB of the 12 GB
        Assert.InRange(budget, (long)(11.6 * 1024 * 1024 * 1024), total);
    }

    [Fact]
    public void CalculateVramBudget_Desktop16Gb_LeavesSafetyMargin()
    {
        // 16 GB card with 2.5 GB used by desktop compositor / browser
        long total = 16L * 1024 * 1024 * 1024;
        long used = (long)(2.5 * 1024 * 1024 * 1024);
        long free = total - used; // 13.5 GB
        long budget = OnnxTiler.CalculateVramBudget(total, used);

        // Budget should be ~11.1 GB (leaving a 15% / 2.4 GB safety margin to prevent AMDGPU/Intel GTT eviction at 85% VRAM)
        Assert.InRange(
            budget,
            (long)(10.8 * 1024 * 1024 * 1024),
            (long)(11.5 * 1024 * 1024 * 1024)
        );
        Assert.True(budget < free);
    }

    [Fact]
    public void CalculateVramBudget_CustomUtilizationFraction_AppliesFraction()
    {
        long total = 16L * 1024 * 1024 * 1024;
        long used = 2L * 1024 * 1024 * 1024; // 14 GB free
        long free = total - used;

        // Custom 100% utilization fraction (e.g. for dedicated worker)
        long budget100 = OnnxTiler.CalculateVramBudget(total, used, customUtilizationFraction: 1.0);
        Assert.Equal(free, budget100);

        // Custom 80% utilization fraction
        long budget80 = OnnxTiler.CalculateVramBudget(total, used, customUtilizationFraction: 0.80);
        Assert.Equal((long)(free * 0.80), budget80);
    }

    [Fact]
    public void CalculateVramBudget_CustomSafetyMargin_SubtractsMargin()
    {
        long total = 16L * 1024 * 1024 * 1024;
        long used = 2L * 1024 * 1024 * 1024; // 14 GB free
        long free = total - used;
        long margin = 1024L * 1024 * 1024; // 1 GB safety margin

        long budget = OnnxTiler.CalculateVramBudget(total, used, customSafetyMarginBytes: margin);
        Assert.Equal(free - margin, budget);
    }

    [Fact]
    public void CalculateVramBudget_SharedCardWithBoundedPartner_CanStayExclusive()
    {
        // A video transcoder reliably holds ~700 MB. With the default threshold the card is treated
        // as a desktop and the budget throws away 1.5-3 GiB; raising the threshold recovers it.
        long total = 12L * 1024 * 1024 * 1024;
        long used = 700L * 1024 * 1024;

        long guarded = OnnxTiler.CalculateVramBudget(total, used);
        long exclusive = OnnxTiler.CalculateVramBudget(
            total,
            used,
            exclusiveThresholdBytes: 1024L * 1024 * 1024
        );

        // guarded = 12 GiB - 700 MiB - 1.8 GiB (15% desktop margin), exclusive = ... - 256 MiB
        Assert.InRange(guarded, (long)(9.3 * 1024 * 1024 * 1024), (long)(9.7 * 1024 * 1024 * 1024));
        Assert.InRange(exclusive, (long)(10.8 * 1024 * 1024 * 1024), total);
        Assert.True(
            exclusive > guarded,
            $"raising the exclusive threshold should recover the desktop margin: {exclusive} vs {guarded}"
        );
    }

    [Fact]
    public void CalculateVramBudget_ThresholdAtOrBelow_IsInclusive()
    {
        // "At or below" the threshold, not "below": usage exactly equal to the threshold still counts
        // as quasi-exclusive, and one byte more does not.
        long total = 12L * 1024 * 1024 * 1024;
        long threshold = 700L * 1024 * 1024;

        long atThreshold = OnnxTiler.CalculateVramBudget(
            total,
            threshold,
            exclusiveThresholdBytes: threshold
        );
        long aboveThreshold = OnnxTiler.CalculateVramBudget(
            total,
            threshold + 1,
            exclusiveThresholdBytes: threshold
        );

        Assert.True(
            atThreshold > aboveThreshold,
            $"expected the exclusive margin at exactly the threshold, got {atThreshold} vs {aboveThreshold}"
        );
    }

    [Fact]
    public void CalculateVramBudget_CustomMarginOrFraction_BeatsTheThreshold()
    {
        // An explicit margin or fraction still wins over both branches, so the threshold cannot
        // silently override a deliberate setting.
        long total = 12L * 1024 * 1024 * 1024;
        long used = 700L * 1024 * 1024;

        long withMargin = OnnxTiler.CalculateVramBudget(
            total,
            used,
            customSafetyMarginBytes: 2L * 1024 * 1024 * 1024,
            exclusiveThresholdBytes: 1024L * 1024 * 1024
        );
        long withFraction = OnnxTiler.CalculateVramBudget(
            total,
            used,
            customUtilizationFraction: 0.75,
            exclusiveThresholdBytes: 1024L * 1024 * 1024
        );

        Assert.Equal(total - used - 2L * 1024 * 1024 * 1024, withMargin);
        Assert.Equal((long)((total - used) * 0.75), withFraction);
    }

    [Fact]
    public void CalculateVramBudget_NonPositiveThreshold_FallsBackToDefault()
    {
        // 0 or a negative value must not make every card "exclusive".
        long total = 12L * 1024 * 1024 * 1024;
        long used = 700L * 1024 * 1024;

        long withZero = OnnxTiler.CalculateVramBudget(total, used, exclusiveThresholdBytes: 0);
        long withNegative = OnnxTiler.CalculateVramBudget(total, used, exclusiveThresholdBytes: -1);
        long withDefault = OnnxTiler.CalculateVramBudget(total, used);

        Assert.Equal(withDefault, withZero);
        Assert.Equal(withDefault, withNegative);
    }

    [Theory]
    [InlineData(null, null, 1024L * 1024 * 1024, true)] // the §4.3 row: threshold alone
    [InlineData(0.5, null, null, true)] // fraction alone
    [InlineData(null, 0L, null, true)] // a zero margin is still an explicit margin
    [InlineData(null, null, 0L, false)] // 0 means "use the default threshold"
    [InlineData(null, null, -1L, false)] // negative means "use the default threshold"
    [InlineData(0.0, -1L, null, false)] // values CalculateVramBudget ignores do not count
    [InlineData(null, null, null, false)] // nothing set keeps the driver's own budget
    public void HasExplicitVramBudgetOverride_MatchesWhatCalculateVramBudgetWouldActOn(
        double? utilizationFraction,
        long? safetyMarginBytes,
        long? exclusiveThresholdBytes,
        bool expected
    )
    {
        // GetAvailableVramBudget's P/Invoke cannot be driven without a real Vulkan device, so the
        // branch it takes when VK_EXT_memory_budget answers is pinned through the predicate it
        // delegates to (and, for the exclusive/shared decision itself, through
        // BudgetFromVulkanMetrics). Before the fix, a threshold-only override was NOT explicit here,
        // so the driver's raw BudgetBytes was returned and the threshold was silently ignored.
        Assert.Equal(
            expected,
            OnnxTiler.HasExplicitVramBudgetOverride(
                utilizationFraction,
                safetyMarginBytes,
                exclusiveThresholdBytes
            )
        );
    }

    [Fact]
    public void CalculateVramBudget_DocumentedSharedCardRow_ThresholdAloneIsExclusive()
    {
        // docs/UPSCALING_MEMORY_MODEL.md §4.3: on a 12 GB card with 700 MB already used, raising the
        // exclusive threshold to 1 GiB moves the budget from the desktop branch (~9.5 GB) to the
        // exclusive branch (~11.1 GB). Only the threshold is set, which is exactly the case
        // GetAvailableVramBudget used to route around CalculateVramBudget.
        long total = 12L * 1024 * 1024 * 1024;
        long used = 700L * 1024 * 1024;

        long exclusive = OnnxTiler.CalculateVramBudget(
            total,
            used,
            exclusiveThresholdBytes: 1024L * 1024 * 1024
        );
        long desktop = OnnxTiler.CalculateVramBudget(total, used);

        Assert.InRange(exclusive, (long)(11.0 * 1024 * 1024 * 1024), total);
        Assert.InRange(desktop, (long)(9.3 * 1024 * 1024 * 1024), (long)(9.7 * 1024 * 1024 * 1024));
        Assert.True(
            exclusive > desktop,
            $"the 1 GiB threshold should recover the desktop margin: {exclusive} vs {desktop}"
        );
    }

    [Fact]
    public void BudgetFromVulkanMetrics_ExclusiveCard_UsesTheDriverBudget()
    {
        // Nothing else on the card: the driver's number is total minus used, and spending it is what
        // an operator who wants the whole card asked for. 16 GB card, 200 MB used -> exclusive.
        long total = 16L * 1024 * 1024 * 1024;
        long used = 200L * 1024 * 1024;
        var metrics = new VulkanGpuMemory(0, "gpu", 0x1002, total, total - used, used);

        Assert.Equal(total - used, OnnxTiler.BudgetFromVulkanMetrics(metrics));
    }

    [Fact]
    public void BudgetFromVulkanMetrics_SharedCard_AppliesTheEvictionMargin()
    {
        // A desktop holding ~3 GB. The driver reports total-minus-used with no eviction headroom of
        // its own, and spending it drives the card to where the driver starts evicting into GTT —
        // measured on an RX 9070 XT that is how a page reached 16.2 GB VRAM and 275 MB of GTT against
        // a 13.1 GB driver budget. The calculated margin has to cap it.
        long total = 16L * 1024 * 1024 * 1024;
        long used = 3L * 1024 * 1024 * 1024;
        long driverBudget = total - used;
        var metrics = new VulkanGpuMemory(0, "gpu", 0x1002, total, driverBudget, used);

        long budget = OnnxTiler.BudgetFromVulkanMetrics(metrics);

        Assert.Equal(OnnxTiler.CalculateVramBudget(total, used), budget);
        Assert.True(
            budget < driverBudget,
            $"expected the eviction margin to cap the driver budget, got {budget} vs {driverBudget}"
        );
    }

    [Fact]
    public void BudgetFromVulkanMetrics_SharedCardWithABoundedPartner_CanStayExclusive()
    {
        // A partner whose footprint is known and bounded does not need the desktop margin: raising
        // the threshold above it puts the card back in the exclusive branch, which keeps only the
        // minimal reserve. The driver budget is then trusted to within that reserve, and far more of
        // the card comes back than the desktop margin would allow.
        long total = 16L * 1024 * 1024 * 1024;
        long used = 700L * 1024 * 1024;
        long driverBudget = total - used;
        var metrics = new VulkanGpuMemory(0, "gpu", 0x1002, total, driverBudget, used);

        long exclusive = OnnxTiler.BudgetFromVulkanMetrics(
            metrics,
            exclusiveThresholdBytes: 1L * 1024 * 1024 * 1024
        );
        long desktop = OnnxTiler.BudgetFromVulkanMetrics(metrics);

        Assert.True(
            driverBudget - exclusive <= 256L * 1024 * 1024 + 1,
            $"the exclusive branch should keep only its minimal reserve: {exclusive} vs {driverBudget}"
        );
        Assert.True(
            exclusive > desktop,
            $"the 1 GiB threshold should recover the desktop margin: {exclusive} vs {desktop}"
        );
    }

    [Fact]
    public void BudgetFromVulkanMetrics_ExplicitOverride_BeatsTheDriverBudget()
    {
        long total = 16L * 1024 * 1024 * 1024;
        long used = 3L * 1024 * 1024 * 1024;
        var metrics = new VulkanGpuMemory(0, "gpu", 0x1002, total, total - used, used);

        long budget = OnnxTiler.BudgetFromVulkanMetrics(metrics, utilizationFraction: 0.5);

        Assert.Equal((long)((total - used) * 0.5), budget);
    }

    [Fact]
    public void PlanTileSplit_TinyPageThatSinglePassRejects_DoesNotReturnTheWholePage()
    {
        // Both sides are at or below MinimumTileSize, so the enumeration bounds are 1 and 1 and the
        // (1,1) candidate is skipped — there is no candidate at all. The seed would then be a single
        // tile covering the page, which is the very plan the single-pass gate rejected: a 200x150 page
        // with the heaviest model needs ~2.9 GiB of activation, so against a 3 GiB budget (2.6 GiB
        // after the session reservation, 2.4 GiB of headroom) the gate rejects it while the loop finds
        // nothing. The emergency grid is returned instead.
        string model = "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx";

        OnnxTiler.TileSplit split = OnnxTiler.PlanTileSplit(
            200,
            150,
            4,
            model,
            3L * 1024 * 1024 * 1024,
            isFp16: true
        );

        Assert.False(
            split.Columns == 1 && split.Rows == 1,
            $"the whole page was rejected as a single pass but planned anyway: {split.Columns}x{split.Rows}"
        );
        Assert.Equal(2, split.Columns);
        Assert.Equal(2, split.Rows);
    }

    [Fact]
    public void PlanTileSplit_UsesTheModelsOwnScaleNotTheDeviceDefault()
    {
        // The device default was fitted on a small fp16 ESRGAN (0.90x); the fp32 transformer measured
        // 1.28x. Planning the transformer with the default would produce a grid whose real footprint
        // overruns the budget, which is exactly the spill this resolution exists to prevent.
        long budget = 12L * 1024 * 1024 * 1024;
        string model = "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx";
        long weights = 16L * 1024 * 1024;

        var withModel = new DeviceMemoryProfile
        {
            ActivationScale = 0.90,
            SessionReservationBytes = 120L * 1024 * 1024,
        };
        withModel.Models[DeviceMemoryProfile.ModelKey(model, weights)] = new ModelMemoryProfile
        {
            ActivationScale = 1.28,
            SessionReservationBytes = 9L * 1024 * 1024,
        };

        var defaultOnly = new DeviceMemoryProfile
        {
            ActivationScale = 0.90,
            SessionReservationBytes = 120L * 1024 * 1024,
        };

        OnnxTiler.TileSplit planned = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: false,
            withModel
        );
        OnnxTiler.TileSplit fromDefault = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: false,
            defaultOnly
        );

        Assert.True(
            planned.PaddedTilePixels < fromDefault.PaddedTilePixels,
            $"the model's own factor should shrink the tile: {planned.PaddedTilePixels} vs {fromDefault.PaddedTilePixels}"
        );

        // The default-scaled grid does not actually fit once the model's own factor is applied...
        Assert.True(
            OnnxTiler.PredictTilePeakBytes(fromDefault, 4, model, weights, false, withModel)
                > budget,
            "the default-scaled grid was expected to overrun the budget at the model's real factor"
        );
        // ...and the grid planned with that factor does fit.
        Assert.True(
            OnnxTiler.PredictTilePeakBytes(planned, 4, model, weights, false, withModel) <= budget,
            "the model-scaled grid was expected to fit the budget"
        );
    }

    [Fact]
    public void PredictTilePeakBytes_FollowsTheGridAndTheProfile()
    {
        string model = "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx";
        long weights = 34L * 1024 * 1024;
        var split = new OnnxTiler.TileSplit(2, 2, 563, 800);

        long builtin = OnnxTiler.PredictTilePeakBytes(split, 4, model, weights, isFp16: true);

        var heavier = new DeviceMemoryProfile
        {
            ActivationScale = 2.0,
            SessionReservationBytes = 100L * 1024 * 1024,
        };
        long withProfile = OnnxTiler.PredictTilePeakBytes(
            split,
            4,
            model,
            weights,
            isFp16: true,
            heavier
        );

        // The prediction has to track the padded tensor, not the raw tile: the same grid inferred at
        // fp32 costs twice the activation term.
        long fp32 = OnnxTiler.PredictTilePeakBytes(split, 4, model, weights, isFp16: false);
        Assert.True(fp32 > builtin, $"fp32 should cost more than fp16: {fp32} vs {builtin}");
        Assert.True(
            withProfile > builtin,
            $"a 2x factor should cost more: {withProfile} vs {builtin}"
        );
    }

    [Fact]
    public void GetGpuVramInfo_OnSupportedHost_DiscoversValidVram()
    {
        // A GPU-less CI has no VRAM to report and GetGpuVramInfo answers (0, 0, 0) rather than
        // throwing. The pre-fix body wrapped the assertions in `if (total > 0)` with no else, so on
        // such a host the test silently passed while asserting nothing. Skipping makes the absent
        // precondition explicit in the run output instead.
        var (total, used, free) = OnnxTiler.GetGpuVramInfo(0);
        Assert.SkipWhen(total == 0, NoGpuMemoryReporting);

        Assert.True(total >= used);

        // free is the driver's budget when the driver reports one, and that budget is
        // deliberately below total - used: it is what is left after the desktop's and any other
        // tenant's reservation. The raw difference only matches on an idle card, so asserting
        // equality here made this test fail whenever anything else on the machine was using the
        // GPU, which is most of the time on a desktop.
        Assert.InRange(free, 0, total - used);
    }

    private string CreateDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"tiler_tests_{Guid.NewGuid():N}");
        _created.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _created)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
