using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Direction for tile overlap blending.
/// </summary>
public enum BlendDirection
{
    Horizontal,
    Vertical,
}

/// <summary>
/// Overlap padding dimensions in pixels.
/// </summary>
public readonly record struct TileOverlap(int Start, int End)
{
    public int Total => Start + End;
}

/// <summary>
/// Blends overlapping tiles using half-sine weighting to eliminate visible seams,
/// matching the original MangaJaNaiConverterGui TileBlender implementation.
/// </summary>
public sealed class TileBlender
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly BlendDirection _direction;
    private readonly byte[] _result;
    private int _offset;
    private int _lastEndOverlap;

    public TileBlender(int width, int height, int channels, BlendDirection direction)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _direction = direction;
        _result = new byte[width * height * channels];
        _offset = 0;
        _lastEndOverlap = 0;
    }

    public int Width => _width;
    public int Height => _height;
    public int Offset => _offset;

    public void AddTile(byte[] tileBytes, int tileWidth, int tileHeight, TileOverlap overlap)
    {
        TileOverlap o = overlap;

        if (_direction == BlendDirection.Horizontal)
        {
            if (tileHeight != _height)
            {
                throw new ArgumentException(
                    $"Tile height {tileHeight} does not match blender height {_height}."
                );
            }

            int tileXStart = 0;
            int currentTileWidth = tileWidth;

            if (_offset == 0)
            {
                for (int y = 0; y < _height; y++)
                {
                    int srcOff = (y * tileWidth) * _channels;
                    int dstOff = (y * _width) * _channels;
                    Buffer.BlockCopy(
                        tileBytes,
                        srcOff,
                        _result,
                        dstOff,
                        currentTileWidth * _channels
                    );
                }

                _offset += currentTileWidth - o.End;
                _lastEndOverlap = o.End;
            }
            else
            {
                if (_lastEndOverlap < o.Start)
                {
                    int diff = o.Start - _lastEndOverlap;
                    tileXStart = diff;
                    currentTileWidth -= diff;
                    o = new TileOverlap(_lastEndOverlap, o.End);
                }

                int nonBlendWidth = currentTileWidth - o.Start * 2;
                if (nonBlendWidth > 0)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        int srcOff = (y * tileWidth + tileXStart + o.Start * 2) * _channels;
                        int dstOff = (y * _width + _offset + o.Start) * _channels;
                        Buffer.BlockCopy(
                            tileBytes,
                            srcOff,
                            _result,
                            dstOff,
                            nonBlendWidth * _channels
                        );
                    }
                }

                int blendSize = o.Start * 2;
                if (blendSize > 0)
                {
                    float[] blend = GetBlendWeights(blendSize);
                    for (int y = 0; y < _height; y++)
                    {
                        int dstRowStart = (y * _width + (_offset - o.Start)) * _channels;
                        int srcRowStart = (y * tileWidth + tileXStart) * _channels;

                        for (int x = 0; x < blendSize; x++)
                        {
                            float b = blend[x];
                            float invB = 1.0f - b;
                            int dstIdx = dstRowStart + x * _channels;
                            int srcIdx = srcRowStart + x * _channels;

                            for (int c = 0; c < _channels; c++)
                            {
                                float val = _result[dstIdx + c] * invB + tileBytes[srcIdx + c] * b;
                                _result[dstIdx + c] = (byte)
                                    Math.Clamp((int)MathF.Round(val), 0, 255);
                            }
                        }
                    }
                }

                _offset += currentTileWidth - o.Total;
                _lastEndOverlap = o.End;
            }
        }
        else // Vertical
        {
            if (tileWidth != _width)
            {
                throw new ArgumentException(
                    $"Tile width {tileWidth} does not match blender width {_width}."
                );
            }

            int tileYStart = 0;
            int currentTileHeight = tileHeight;

            if (_offset == 0)
            {
                Buffer.BlockCopy(tileBytes, 0, _result, 0, _width * currentTileHeight * _channels);
                _offset += currentTileHeight - o.End;
                _lastEndOverlap = o.End;
            }
            else
            {
                if (_lastEndOverlap < o.Start)
                {
                    int diff = o.Start - _lastEndOverlap;
                    tileYStart = diff;
                    currentTileHeight -= diff;
                    o = new TileOverlap(_lastEndOverlap, o.End);
                }

                int nonBlendHeight = currentTileHeight - o.Start * 2;
                if (nonBlendHeight > 0)
                {
                    int srcOff = (tileYStart + o.Start * 2) * _width * _channels;
                    int dstOff = (_offset + o.Start) * _width * _channels;
                    int byteCount = nonBlendHeight * _width * _channels;
                    Buffer.BlockCopy(tileBytes, srcOff, _result, dstOff, byteCount);
                }

                int blendSize = o.Start * 2;
                if (blendSize > 0)
                {
                    float[] blend = GetBlendWeights(blendSize);
                    int rowBytes = _width * _channels;

                    for (int y = 0; y < blendSize; y++)
                    {
                        float b = blend[y];
                        float invB = 1.0f - b;
                        int dstRowStart = (_offset - o.Start + y) * rowBytes;
                        int srcRowStart = (tileYStart + y) * rowBytes;

                        for (int i = 0; i < rowBytes; i++)
                        {
                            float val =
                                _result[dstRowStart + i] * invB + tileBytes[srcRowStart + i] * b;
                            _result[dstRowStart + i] = (byte)
                                Math.Clamp((int)MathF.Round(val), 0, 255);
                        }
                    }
                }

                _offset += currentTileHeight - o.Total;
                _lastEndOverlap = o.End;
            }
        }
    }

    public byte[] GetResult()
    {
        return _result;
    }

    public static float[] GetBlendWeights(int blendSize)
    {
        if (blendSize <= 0)
        {
            return Array.Empty<float>();
        }

        if (blendSize == 1)
        {
            return [0.5f];
        }

        float[] blend = new float[blendSize];
        float denom = blendSize - 1;
        for (int i = 0; i < blendSize; i++)
        {
            float t = i / denom;
            blend[i] = HalfSinBlend(t);
        }

        return blend;
    }

    public static float HalfSinBlend(float t)
    {
        float clipped = Math.Clamp(t * 2f - 0.5f, 0f, 1f);
        return (MathF.Sin(clipped * MathF.PI - MathF.PI / 2f) + 1f) / 2f;
    }
}

/// <summary>
/// Known neural network architectures used by MangaJaNai and IllustrationJaNai models.
/// </summary>
public enum ModelArchitecture
{
    Unknown,
    Esrgan,
    Span,
    FdatM,
    FdatXl,
    Dat2,
    HatL,
}

/// <summary>
/// Provides tiling, reflect padding, and output reconstruction for ONNX upscaling models.
/// </summary>
public static class OnnxTiler
{
    /// <summary>
    /// Default tile size constraint. 0 means auto-estimate safe tile size based on model architecture and VRAM.
    /// </summary>
    public const int DefaultTileSize = 0;

    /// <summary>
    /// Default overlap padding in pixels between adjacent tiles.
    /// </summary>
    public const int DefaultTilePad = 16;

    /// <summary>
    /// Detects the neural network model architecture from its name or file path.
    /// </summary>
    public static ModelArchitecture DetectArchitecture(string modelNameOrPath)
    {
        string name = Path.GetFileNameWithoutExtension(modelNameOrPath);
        if (name.Contains("SPAN", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.Span;
        if (name.Contains("HAT", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.HatL;
        if (
            name.Contains("DAT2", StringComparison.OrdinalIgnoreCase)
            || (
                name.Contains("DAT", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("FDAT", StringComparison.OrdinalIgnoreCase)
            )
        )
            return ModelArchitecture.Dat2;
        if (name.Contains("FDAT_XL", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.FdatXl;
        if (name.Contains("FDAT", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.FdatM;
        if (
            name.Contains("ESRGAN", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MangaJaNai", StringComparison.OrdinalIgnoreCase)
        )
            return ModelArchitecture.Esrgan;

        return ModelArchitecture.Unknown;
    }

    /// <summary>
    /// Gets the ratio of peak intermediate activation memory to input tensor bytes for a given architecture and scale.
    /// </summary>
    public static double GetMemoryMultiplier(
        ModelArchitecture architecture,
        int scale,
        long modelSizeBytes = 0
    )
    {
        return architecture switch
        {
            ModelArchitecture.Span => scale >= 4 ? 100.0 : 50.0,
            ModelArchitecture.FdatM => scale >= 4 ? 380.0 : 200.0,
            ModelArchitecture.FdatXl => scale >= 4 ? 700.0 : 400.0,
            ModelArchitecture.Dat2 => scale >= 4 ? 800.0 : 450.0,
            ModelArchitecture.HatL => scale >= 4 ? 2200.0 : 1200.0,
            ModelArchitecture.Esrgan => scale >= 4 ? 1250.0 : 750.0,
            _ => modelSizeBytes > 0 ? (modelSizeBytes / (1024.0 * 52.0)) : 1000.0,
        };
    }

    /// <summary>
    /// Gets the empirically validated maximum safe tile dimension in pixels for a given architecture and scale,
    /// preventing execution provider GPU memory allocations from exceeding driver chunk sizes and spilling into GTT.
    /// </summary>
    public static int GetMaxTileSizeForArchitecture(ModelArchitecture architecture, int scale)
    {
        if (scale <= 2)
        {
            return architecture switch
            {
                ModelArchitecture.Span => 1280,
                ModelArchitecture.Esrgan => 896,
                ModelArchitecture.FdatM => 640,
                ModelArchitecture.FdatXl => 512,
                ModelArchitecture.Dat2 => 512,
                ModelArchitecture.HatL => 384,
                _ => 896,
            };
        }

        return architecture switch
        {
            ModelArchitecture.Span => 1024,
            ModelArchitecture.Esrgan => 448,
            ModelArchitecture.FdatM => 384,
            ModelArchitecture.FdatXl => 320,
            ModelArchitecture.Dat2 => 256,
            ModelArchitecture.HatL => 256,
            _ => 448,
        };
    }

    /// <summary>
    /// Estimates peak activation memory in bytes required to upscale an image of the given dimensions.
    /// </summary>
    public static long EstimatePeakMemoryBytes(
        int width,
        int height,
        int scale,
        string modelNameOrPath,
        long modelSizeBytes = 0,
        bool isFp16 = false
    )
    {
        var arch = DetectArchitecture(modelNameOrPath);
        double multiplier = GetMemoryMultiplier(arch, scale, modelSizeBytes);
        int elementSize = isFp16 ? 2 : 4;
        long inputBytes = (long)width * height * 3 * elementSize;
        return (long)(inputBytes * multiplier);
    }

    /// <summary>
    /// Estimates a safe maximum tile dimension in pixels based on model architecture, scale,
    /// image dimensions, precision (FP16/FP32), and available memory budget.
    /// If the whole image fits safely within the budget and architecture limits, returns 0 (no tiling needed).
    /// </summary>
    public static int EstimateTileSize(
        int width,
        int height,
        int scale,
        string modelNameOrPath,
        long memoryBudgetBytes,
        long modelSizeBytes = 0,
        bool isFp16 = false
    )
    {
        var arch = DetectArchitecture(modelNameOrPath);
        int maxAllowed = GetMaxTileSizeForArchitecture(arch, scale);

        long peakMemory = EstimatePeakMemoryBytes(
            width,
            height,
            scale,
            modelNameOrPath,
            modelSizeBytes,
            isFp16
        );
        if (peakMemory <= memoryBudgetBytes)
        {
            return 0; // Whole image fits safely within budget (single pass)!
        }

        double multiplier = GetMemoryMultiplier(arch, scale, modelSizeBytes);
        int elementSize = isFp16 ? 2 : 4;

        double tilePixels = memoryBudgetBytes / (3.0 * elementSize * multiplier);
        int tileDim = (int)Math.Sqrt(Math.Max(16.0, tilePixels));

        // Snap to multiple of 64, clamped between architecture safe limits
        int snapped = (tileDim / 64) * 64;
        return Math.Clamp(snapped, Math.Min(256, maxAllowed), maxAllowed);
    }

    /// <summary>
    /// Discovers available GPU VRAM on Linux via AMDGPU/DRM sysfs, using at most 40% of free VRAM
    /// (capped at 4 GiB) to avoid TTM memory manager spilling allocations into GTT.
    /// Falls back to a safe 3 GiB default budget on other platforms.
    /// </summary>
    public static long GetAvailableVramBudget()
    {
        try
        {
            if (Directory.Exists("/sys/class/drm"))
            {
                var cards = Directory.GetDirectories("/sys/class/drm", "card*");
                foreach (var card in cards)
                {
                    string totalPath = Path.Combine(card, "device", "mem_info_vram_total");
                    string usedPath = Path.Combine(card, "device", "mem_info_vram_used");
                    if (File.Exists(totalPath))
                    {
                        if (
                            long.TryParse(
                                File.ReadAllText(totalPath).Trim(),
                                CultureInfo.InvariantCulture,
                                out long total
                            )
                            && total > 0
                        )
                        {
                            long used = 0;
                            if (File.Exists(usedPath))
                            {
                                long.TryParse(
                                    File.ReadAllText(usedPath).Trim(),
                                    CultureInfo.InvariantCulture,
                                    out used
                                );
                            }

                            long free = Math.Max(0, total - used);
                            if (free > 0)
                            {
                                // Use at most 40% of free VRAM, capped at 4 GiB
                                return Math.Min((long)(free * 0.40), 4L * 1024 * 1024 * 1024);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        // Safe fallback for systems where sysfs is not accessible or on CPU/DirectML: 3 GiB
        return 3L * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// Reads current VRAM and GTT used in bytes from Linux AMDGPU sysfs, or returns (0, 0) if unavailable.
    /// </summary>
    public static (long VramUsed, long GttUsed) GetGpuMemoryUsage()
    {
        try
        {
            if (Directory.Exists("/sys/class/drm"))
            {
                var cards = Directory.GetDirectories("/sys/class/drm", "card*");
                foreach (var card in cards)
                {
                    string vramPath = Path.Combine(card, "device", "mem_info_vram_used");
                    string gttPath = Path.Combine(card, "device", "mem_info_gtt_used");
                    if (File.Exists(vramPath) && File.Exists(gttPath))
                    {
                        long.TryParse(
                            File.ReadAllText(vramPath).Trim(),
                            CultureInfo.InvariantCulture,
                            out long vram
                        );
                        long.TryParse(
                            File.ReadAllText(gttPath).Trim(),
                            CultureInfo.InvariantCulture,
                            out long gtt
                        );
                        return (vram, gtt);
                    }
                }
            }
        }
        catch { }

        return (0, 0);
    }

    /// <summary>
    /// Upscales an RGB image represented by raw interleaved RGB byte array.
    /// When tileSize &lt;= 0, attempts whole-image inference in a single pass.
    /// When tileSize &gt; 0, partitions the image into symmetric tiles bounded by tileSize
    /// with smooth half-sine overlap blending to eliminate visible seams.
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
        int currentMaxTileSizeX = tileSize > 0 ? tileSize : width;
        int currentMaxTileSizeY = tileSize > 0 ? tileSize : height;

        while (true)
        {
            try
            {
                return AutoSplit(
                    rgbBytes,
                    width,
                    height,
                    scale,
                    session,
                    currentMaxTileSizeX,
                    currentMaxTileSizeY,
                    tilePad,
                    cancellationToken
                );
            }
            catch (Exception ex)
                when (IsMemoryException(ex)
                    && (currentMaxTileSizeX > 16 || currentMaxTileSizeY > 16)
                )
            {
                currentMaxTileSizeX = Math.Max(16, currentMaxTileSizeX / 2);
                currentMaxTileSizeY = Math.Max(16, currentMaxTileSizeY / 2);
                GC.Collect();
            }
        }
    }

    private static byte[] AutoSplit(
        byte[] rgbBytes,
        int width,
        int height,
        int scale,
        InferenceSession session,
        int maxTileSizeX,
        int maxTileSizeY,
        int overlap,
        CancellationToken cancellationToken
    )
    {
        int tileCountX = (int)Math.Ceiling((double)width / maxTileSizeX);
        int tileCountY = (int)Math.Ceiling((double)height / maxTileSizeY);
        int tileSizeX = (int)Math.Ceiling((double)width / tileCountX);
        int tileSizeY = (int)Math.Ceiling((double)height / tileCountY);

        int outWidth = width * scale;
        int outHeight = height * scale;

        if (tileCountX <= 1 && tileCountY <= 1)
        {
            return UpscaleTile(rgbBytes, width, height, scale, session, cancellationToken);
        }

        // Determine uniform padded dimensions across all tiles in this image.
        // Passing uniform tensor shapes to ONNX Runtime (WebGPU / Dawn EP) prevents
        // per-tile buffer re-allocation and memory arena accumulation in Vulkan VRAM/GTT.
        int maxPaddedW = tileSizeX + 2 * overlap;
        int maxPaddedH = tileSizeY + 2 * overlap;
        int uniformTargetDim = Math.Max(maxPaddedW, maxPaddedH);
        int uniformTargetSize = ((uniformTargetDim + 63) / 64) * 64;

        var imageBlender = new TileBlender(outWidth, outHeight, 3, BlendDirection.Vertical);

        for (int y = 0; y < tileCountY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int tileY = y * tileSizeY;
            int tileH = Math.Min(tileSizeY, height - tileY);
            int padTop = Math.Min(tileY, overlap);
            int padBottom = Math.Min(height - (tileY + tileH), overlap);
            int paddedH = tileH + padTop + padBottom;

            var rowBlender = new TileBlender(
                outWidth,
                paddedH * scale,
                3,
                BlendDirection.Horizontal
            );
            var rowOverlap = new TileOverlap(padTop * scale, padBottom * scale);

            for (int x = 0; x < tileCountX; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int tileX = x * tileSizeX;
                int tileW = Math.Min(tileSizeX, width - tileX);
                int padLeft = Math.Min(tileX, overlap);
                int padRight = Math.Min(width - (tileX + tileW), overlap);
                int paddedW = tileW + padLeft + padRight;

                int paddedX = tileX - padLeft;
                int paddedY = tileY - padTop;

                byte[] crop = ExtractCrop(rgbBytes, width, paddedX, paddedY, paddedW, paddedH);
                byte[] upscaledTile = UpscaleTile(
                    crop,
                    paddedW,
                    paddedH,
                    scale,
                    session,
                    cancellationToken,
                    targetW: uniformTargetSize,
                    targetH: uniformTargetSize
                );

                var tileOverlap = new TileOverlap(padLeft * scale, padRight * scale);
                rowBlender.AddTile(upscaledTile, paddedW * scale, paddedH * scale, tileOverlap);
            }

            imageBlender.AddTile(rowBlender.GetResult(), outWidth, paddedH * scale, rowOverlap);
        }

        return imageBlender.GetResult();
    }

    public static bool IsMemoryException(Exception ex)
    {
        if (ex is OutOfMemoryException)
        {
            return true;
        }

        if (ex is OnnxRuntimeException onnxEx)
        {
            string msg = onnxEx.Message;
            if (
                msg.Contains("allocate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("hipMalloc", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("VK_ERROR_OUT_OF", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("command submission", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("raw_hash_map", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("allocation failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("DXGI_ERROR", StringComparison.OrdinalIgnoreCase)
                || msg.Contains(
                    "CL_MEM_OBJECT_ALLOCATION_FAILURE",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
        }

        if (ex.InnerException != null && IsMemoryException(ex.InnerException))
        {
            return true;
        }

        return false;
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
        CancellationToken cancellationToken,
        int targetW = 0,
        int targetH = 0
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        int padH = (16 - (tileH % 16)) % 16;
        int padW = (16 - (tileW % 16)) % 16;
        int paddedW = Math.Max(targetW, tileW + padW);
        int paddedH = Math.Max(targetH, tileH + padH);

        byte[] paddedInput;
        if (paddedH == tileH && paddedW == tileW)
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
            var inVal = NamedOnnxValue.CreateFromTensor("input", halfTensor);
            outputs = session.Run([inVal]);
        }
        else
        {
            var floatTensor = new DenseTensor<float>(tensorData, [1, 3, paddedH, paddedW]);
            var inVal = NamedOnnxValue.CreateFromTensor("input", floatTensor);
            outputs = session.Run([inVal]);
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
