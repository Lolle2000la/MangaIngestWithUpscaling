using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class OnnxTilerTests
{
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

    [Fact]
    [Trait("Category", "Integration")]
    public void UpscaleRgb_ProducesConsistentOutputBetweenSinglePassAndTiled()
    {
        string modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../test_data/models/2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16.onnx"
        );
        if (!File.Exists(modelPath))
        {
            modelPath = "test_data/models/2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16.onnx";
            if (!File.Exists(modelPath))
            {
                return;
            }
        }

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
        // 1600x2400 on 4x ESRGAN (FP32)
        long esrgan4xPeak = OnnxTiler.EstimatePeakMemoryBytes(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            isFp16: false
        );
        // ~85-92 GB peak activation memory for full 1600x2400 page at 4x
        Assert.InRange(esrgan4xPeak, 70L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);

        // 1600x2400 on 2x SPAN (FP16)
        long span2xPeak = OnnxTiler.EstimatePeakMemoryBytes(
            1600,
            2400,
            2,
            "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
            isFp16: true
        );
        // ~1.3 GB
        Assert.InRange(span2xPeak, 1L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void EstimateTileSize_Span_ReturnsZeroForFullImageUnderBudget()
    {
        // SPAN requires ~1.3 GB on 1600x2400, well below a 4 GB budget -> single pass
        int tileSize = OnnxTiler.EstimateTileSize(
            1600,
            2400,
            2,
            "2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16.onnx",
            memoryBudgetBytes: 4L * 1024 * 1024 * 1024,
            isFp16: true
        );
        Assert.Equal(0, tileSize);
    }

    [Fact]
    public void EstimateTileSize_Esrgan4x_ReturnsSafeTileSize()
    {
        // ESRGAN 4x under a 5 GiB budget dynamically yields 448x448 tiles
        int tileSize = OnnxTiler.EstimateTileSize(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 5L * 1024 * 1024 * 1024,
            isFp16: false
        );
        Assert.Equal(448, tileSize);
        Assert.Equal(0, tileSize % 64);
    }

    [Fact]
    public void EstimateTileSize_Esrgan2x_AllowsLargerTileSize()
    {
        // ESRGAN 2x under a 5 GiB budget dynamically yields 896x896 tiles (scale^2 scaling)
        int tileSize = OnnxTiler.EstimateTileSize(
            1600,
            2400,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            memoryBudgetBytes: 5L * 1024 * 1024 * 1024,
            isFp16: false
        );
        Assert.Equal(896, tileSize);
        Assert.Equal(0, tileSize % 64);
    }

    [Fact]
    public void EstimateTileSize_SmallImage_ReturnsZero()
    {
        // Small 64x64 icon fits in memory under any budget -> single pass
        int tileSize = OnnxTiler.EstimateTileSize(
            64,
            64,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 4L * 1024 * 1024 * 1024,
            isFp16: false
        );
        Assert.Equal(0, tileSize);
    }

    [Fact]
    public void EstimateTileSize_MangaJaNai_FP16_HalvesPeakMemoryAndExpandsTileSize()
    {
        long peakFp32 = OnnxTiler.EstimatePeakMemoryBytes(
            1125,
            1600,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            isFp16: false
        );
        long peakFp16 = OnnxTiler.EstimatePeakMemoryBytes(
            1125,
            1600,
            2,
            "2x_MangaJaNai_1600p_V1_ESRGAN_90k.onnx",
            isFp16: true
        );

        Assert.Equal(peakFp32 / 2, peakFp16);

        // Under a constrained 3 GiB budget, FP16 yields strictly larger tiles than FP32
        int tileFp32 = OnnxTiler.EstimateTileSize(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 3L * 1024 * 1024 * 1024,
            isFp16: false
        );
        int tileFp16 = OnnxTiler.EstimateTileSize(
            1600,
            2400,
            4,
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx",
            memoryBudgetBytes: 3L * 1024 * 1024 * 1024,
            isFp16: true
        );

        Assert.True(tileFp16 > tileFp32);
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

        // Budget should be ~12.8 GB (utilizing ~95% of the 13.5 GB free VRAM with ~700 MB safety margin)
        Assert.InRange(
            budget,
            (long)(12.5 * 1024 * 1024 * 1024),
            (long)(13.2 * 1024 * 1024 * 1024)
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
    public void GetGpuVramInfo_OnSupportedHost_DiscoversValidVram()
    {
        var (total, used, free) = OnnxTiler.GetGpuVramInfo(0);
        if (total > 0)
        {
            Assert.True(total >= used);
            Assert.Equal(total - used, free);
        }
    }
}
