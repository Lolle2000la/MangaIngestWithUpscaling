using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Provides tiling, reflect padding, and output reconstruction for ONNX upscaling models.
/// </summary>
public static class OnnxTiler
{
    public const int DefaultTileSize = 1024;
    public const int DefaultTilePad = 16;

    /// <summary>
    /// Upscales an RGB image represented by raw interleaved RGB byte array.
    /// Handles reflect padding to multiples of 16 and seamless tiling for large images.
    /// </summary>
    public static byte[] UpscaleRgb(
        byte[] rgbBytes,
        int width,
        int height,
        int scale,
        InferenceSession session,
        int tileSize = DefaultTileSize,
        int tilePad = DefaultTilePad,
        CancellationToken cancellationToken = default
    )
    {
        int outWidth = width * scale;
        int outHeight = height * scale;
        byte[] outBytes = new byte[outWidth * outHeight * 3];

        if (tileSize <= 0 || (width <= tileSize && height <= tileSize))
        {
            return UpscaleTile(rgbBytes, width, height, scale, session, cancellationToken);
        }

        for (int y = 0; y < height; y += tileSize)
        {
            for (int x = 0; x < width; x += tileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int y1 = Math.Max(y - tilePad, 0);
                int x1 = Math.Max(x - tilePad, 0);
                int y2 = Math.Min(y + tileSize + tilePad, height);
                int x2 = Math.Min(x + tileSize + tilePad, width);

                int tileW = x2 - x1;
                int tileH = y2 - y1;

                byte[] tileBytes = ExtractCrop(rgbBytes, width, x1, y1, tileW, tileH);
                byte[] upscaledTile = UpscaleTile(
                    tileBytes,
                    tileW,
                    tileH,
                    scale,
                    session,
                    cancellationToken
                );

                int outY1 = y * scale;
                int outX1 = x * scale;
                int outY2 = Math.Min((y + tileSize) * scale, outHeight);
                int outX2 = Math.Min((x + tileSize) * scale, outWidth);

                int cropY1 = (y - y1) * scale;
                int cropX1 = (x - x1) * scale;
                int copyWidth = (outX2 - outX1) * 3;
                int copyRows = outY2 - outY1;
                int upscaledTileW = tileW * scale;

                for (int row = 0; row < copyRows; row++)
                {
                    int srcOffset = ((cropY1 + row) * upscaledTileW + cropX1) * 3;
                    int dstOffset = ((outY1 + row) * outWidth + outX1) * 3;
                    Buffer.BlockCopy(upscaledTile, srcOffset, outBytes, dstOffset, copyWidth);
                }
            }
        }

        return outBytes;
    }

    public static byte[] ExtractCrop(byte[] src, int srcWidth, int x, int y, int w, int h)
    {
        byte[] dst = new byte[w * h * 3];
        int rowBytes = w * 3;
        for (int r = 0; r < h; r++)
        {
            int srcOff = ((y + r) * srcWidth + x) * 3;
            int dstOff = r * rowBytes;
            Buffer.BlockCopy(src, srcOff, dst, dstOff, rowBytes);
        }
        return dst;
    }

    public static byte[] UpscaleTile(
        byte[] tileBytes,
        int tileW,
        int tileH,
        int scale,
        InferenceSession session,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        int padH = (16 - (tileH % 16)) % 16;
        int padW = (16 - (tileW % 16)) % 16;
        int paddedW = tileW + padW;
        int paddedH = tileH + padH;

        byte[] paddedInput;
        if (padH == 0 && padW == 0)
        {
            paddedInput = tileBytes;
        }
        else
        {
            paddedInput = new byte[paddedW * paddedH * 3];
            for (int py = 0; py < paddedH; py++)
            {
                int srcY = py < tileH ? py : (tileH - 1 - (py - tileH));
                if (srcY < 0)
                    srcY = 0;
                for (int px = 0; px < paddedW; px++)
                {
                    int srcX = px < tileW ? px : (tileW - 1 - (px - tileW));
                    if (srcX < 0)
                        srcX = 0;
                    int srcIdx = (srcY * tileW + srcX) * 3;
                    int dstIdx = (py * paddedW + px) * 3;
                    paddedInput[dstIdx] = tileBytes[srcIdx];
                    paddedInput[dstIdx + 1] = tileBytes[srcIdx + 1];
                    paddedInput[dstIdx + 2] = tileBytes[srcIdx + 2];
                }
            }
        }

        int planeSize = paddedW * paddedH;
        float[] tensorData = new float[1 * 3 * planeSize];
        int rOff = 0;
        int gOff = planeSize;
        int bOff = planeSize * 2;

        for (int i = 0; i < planeSize; i++)
        {
            int bIdx = i * 3;
            tensorData[rOff + i] = paddedInput[bIdx] / 255.0f;
            tensorData[gOff + i] = paddedInput[bIdx + 1] / 255.0f;
            tensorData[bOff + i] = paddedInput[bIdx + 2] / 255.0f;
        }

        cancellationToken.ThrowIfCancellationRequested();

        Type elementType = session.InputMetadata["input"].ElementType;
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;

        if (elementType == typeof(Float16))
        {
            Float16[] halfData = new Float16[tensorData.Length];
            for (int i = 0; i < tensorData.Length; i++)
            {
                halfData[i] = (Float16)tensorData[i];
            }
            var halfTensor = new DenseTensor<Float16>(halfData, [1, 3, paddedH, paddedW]);
            var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", halfTensor) };
            outputs = session.Run(inputs);
        }
        else
        {
            var floatTensor = new DenseTensor<float>(tensorData, [1, 3, paddedH, paddedW]);
            var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", floatTensor) };
            outputs = session.Run(inputs);
        }

        using (outputs)
        {
            DisposableNamedOnnxValue outNamedValue = outputs.First(o => o.Name == "output");
            int outTileW = tileW * scale;
            int outTileH = tileH * scale;
            int outPaddedW = paddedW * scale;
            int outPaddedH = paddedH * scale;
            int outPlane = outPaddedW * outPaddedH;
            byte[] outCrop = new byte[outTileW * outTileH * 3];

            int outROff = 0;
            int outGOff = outPlane;
            int outBOff = outPlane * 2;

            if (outNamedValue.Value is DenseTensor<Float16> halfOutTensor)
            {
                ReadOnlySpan<Float16> halfSpan = halfOutTensor.Buffer.Span;
                for (int oy = 0; oy < outTileH; oy++)
                {
                    int rowOffset = oy * outPaddedW;
                    int dstRowOffset = oy * outTileW;
                    for (int ox = 0; ox < outTileW; ox++)
                    {
                        int pIdx = rowOffset + ox;
                        int dstIdx = (dstRowOffset + ox) * 3;

                        float r = Math.Clamp((float)halfSpan[outROff + pIdx] * 255.0f, 0f, 255f);
                        float g = Math.Clamp((float)halfSpan[outGOff + pIdx] * 255.0f, 0f, 255f);
                        float b = Math.Clamp((float)halfSpan[outBOff + pIdx] * 255.0f, 0f, 255f);

                        outCrop[dstIdx] = (byte)MathF.Round(r);
                        outCrop[dstIdx + 1] = (byte)MathF.Round(g);
                        outCrop[dstIdx + 2] = (byte)MathF.Round(b);
                    }
                }
            }
            else
            {
                var floatOutTensor = (DenseTensor<float>)outNamedValue.AsTensor<float>();
                ReadOnlySpan<float> span = floatOutTensor.Buffer.Span;
                for (int oy = 0; oy < outTileH; oy++)
                {
                    int rowOffset = oy * outPaddedW;
                    int dstRowOffset = oy * outTileW;
                    for (int ox = 0; ox < outTileW; ox++)
                    {
                        int pIdx = rowOffset + ox;
                        int dstIdx = (dstRowOffset + ox) * 3;

                        float r = Math.Clamp(span[outROff + pIdx] * 255.0f, 0f, 255f);
                        float g = Math.Clamp(span[outGOff + pIdx] * 255.0f, 0f, 255f);
                        float b = Math.Clamp(span[outBOff + pIdx] * 255.0f, 0f, 255f);

                        outCrop[dstIdx] = (byte)MathF.Round(r);
                        outCrop[dstIdx + 1] = (byte)MathF.Round(g);
                        outCrop[dstIdx + 2] = (byte)MathF.Round(b);
                    }
                }
            }

            return outCrop;
        }
    }
}
