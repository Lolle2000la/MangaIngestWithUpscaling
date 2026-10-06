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
}
