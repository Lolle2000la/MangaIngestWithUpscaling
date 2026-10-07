using System.Diagnostics;
using System.Globalization;
using MangaIngestWithUpscaling.Shared.Services.GPU;
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
    /// In super-resolution neural networks, peak workspace memory is quadratic with upscale factor (scale^2)
    /// because the output stage convolution workspaces operate on (scale * W) * (scale * H) pixels.
    /// </summary>
    public static double GetMemoryMultiplier(
        ModelArchitecture architecture,
        int scale,
        long modelSizeBytes = 0
    )
    {
        int scaleFactorSq = scale * scale;
        return architecture switch
        {
            ModelArchitecture.Span => 15.0 * scaleFactorSq,
            ModelArchitecture.FdatM => 45.0 * scaleFactorSq,
            ModelArchitecture.FdatXl => 80.0 * scaleFactorSq,
            ModelArchitecture.Dat2 => 90.0 * scaleFactorSq,
            ModelArchitecture.HatL => 175.0 * scaleFactorSq,
            ModelArchitecture.Esrgan => 125.0 * scaleFactorSq,
            _ => (modelSizeBytes > 0 ? (modelSizeBytes / (1024.0 * 52.0)) : 75.0)
                * (scaleFactorSq / 4.0),
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
    /// If the whole image fits safely within the budget, returns 0 (no tiling needed).
    /// Dynamically derives tile size from the memory budget without arbitrary hardcoded limits.
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
        long peakMemory = EstimatePeakMemoryBytes(
            width,
            height,
            scale,
            modelNameOrPath,
            modelSizeBytes,
            isFp16
        );

        // Whole image in a single pass is ONLY safe if the peak activation memory is well below 2.5 GiB
        // (such as small images or lightweight architectures like SPAN).
        // Heavy architectures (ESRGAN, DAT2, HAT) on full manga pages (1125x1600+) require 6-12 GB in FP32,
        // which exhausts GPU memory margins, risks driver spillover into GTT, and breaks uniform buffer reuse.
        if (peakMemory <= Math.Min(memoryBudgetBytes, 2500L * 1024 * 1024))
        {
            return 0; // Fits safely in a single pass without risk of memory pressure
        }

        var arch = DetectArchitecture(modelNameOrPath);
        double multiplier = GetMemoryMultiplier(arch, scale, modelSizeBytes);
        int elementSize = isFp16 ? 2 : 4;
        long staticWeights = modelSizeBytes > 0 ? modelSizeBytes : 70L * 1024 * 1024;
        long availableForActivation = Math.Max(
            100L * 1024 * 1024,
            memoryBudgetBytes - staticWeights
        );

        double tilePixels = availableForActivation / (3.0 * elementSize * multiplier);
        int tileDim = (int)Math.Sqrt(Math.Max(64.0, tilePixels));

        // Snap down to multiple of 64 for optimal GPU tensor alignment
        int snapped = (tileDim / 64) * 64;

        // Cap maximum tile dimension at 1024x1024.
        // Tiles larger than 1024 produce diminishing throughput returns, hit WebGPU descriptor/storage
        // limits, and risk thrashing GPU cache.
        snapped = Math.Min(1024, snapped);

        // Never return less than 128 (tiling below 128 has excessive padding/blending overhead)
        return Math.Max(128, snapped);
    }

    /// <summary>
    /// Calculates the available VRAM budget for tile estimation from total and currently used VRAM.
    /// In dedicated/headless environments (e.g. NAS) with exclusive GPU use (usedVram &lt;= 350 MB),
    /// utilizes up to ~97% of free VRAM (leaving a minimal 256 MB buffer for driver/command submission).
    /// In desktop environments (usedVram &gt; 350 MB) with display compositors/browsers,
    /// utilizes ~95% of free VRAM with an adaptive 512 MB - 1024 MB safety margin to prevent compositor stutters.
    /// Can be explicitly overridden with a custom utilization fraction or safety margin.
    /// </summary>
    public static long CalculateVramBudget(
        long totalVram,
        long usedVram,
        double? customUtilizationFraction = null,
        long? customSafetyMarginBytes = null
    )
    {
        long freeVram = Math.Max(0, totalVram - usedVram);
        if (freeVram <= 0)
        {
            return 4L * 1024 * 1024 * 1024; // Safe fallback
        }

        if (customUtilizationFraction.HasValue && customUtilizationFraction.Value > 0)
        {
            double fraction = Math.Clamp(customUtilizationFraction.Value, 0.1, 1.0);
            return (long)(freeVram * fraction);
        }

        if (customSafetyMarginBytes.HasValue && customSafetyMarginBytes.Value >= 0)
        {
            return Math.Max(256L * 1024 * 1024, freeVram - customSafetyMarginBytes.Value);
        }

        // Automatic adaptive budgeting:
        // When usedVram <= 350 MB: Headless / dedicated environment (e.g. NAS) with exclusive GPU use.
        // Minimal safety margin (256 MB or 3% of free memory) to maximize utilization up to 100%.
        //
        // When usedVram > 350 MB: Desktop environment with active display manager, compositor, or browser.
        // Modest safety margin (5% of free VRAM, clamped between 512 MB and 1024 MB) to prevent
        // desktop compositor stutters or sudden OOM spikes.
        long safetyMargin;
        if (usedVram <= 350L * 1024 * 1024)
        {
            safetyMargin = Math.Min(256L * 1024 * 1024, (long)(freeVram * 0.03));
        }
        else
        {
            safetyMargin = Math.Clamp(
                (long)(freeVram * 0.05),
                512L * 1024 * 1024,
                1024L * 1024 * 1024
            );
        }

        return Math.Max(512L * 1024 * 1024, freeVram - safetyMargin);
    }

    /// <summary>
    /// Discovers available GPU VRAM and computes the safe memory budget for tile size estimation.
    /// Prioritizes Vulkan VK_EXT_memory_budget (driver-calculated memory budget across AMD, NVIDIA, Intel),
    /// falling back to Linux DRM sysfs (AMD/Intel) and nvidia-smi (NVIDIA), and finally a safe default budget.
    /// Automatically adapts to dedicated/NAS vs desktop environments.
    /// </summary>
    public static long GetAvailableVramBudget(
        int deviceId = 0,
        double? utilizationFraction = null,
        long? safetyMarginBytes = null
    )
    {
        // 1. Try Vulkan VK_EXT_memory_budget (primary for all GPUs)
        var vkMem = VulkanMemoryProvider.QueryDevice(deviceId);
        if (vkMem != null && vkMem.TotalVramBytes > 0)
        {
            // If explicit overrides were provided, respect them
            if (
                (utilizationFraction.HasValue && utilizationFraction.Value > 0)
                || (safetyMarginBytes.HasValue && safetyMarginBytes.Value >= 0)
            )
            {
                return CalculateVramBudget(
                    vkMem.TotalVramBytes,
                    vkMem.UsedVramBytes,
                    utilizationFraction,
                    safetyMarginBytes
                );
            }

            // Driver budget is calculated directly by the GPU driver taking OS & desktop into account
            if (vkMem.BudgetBytes > 0)
            {
                return vkMem.BudgetBytes;
            }

            return CalculateVramBudget(
                vkMem.TotalVramBytes,
                vkMem.UsedVramBytes,
                utilizationFraction,
                safetyMarginBytes
            );
        }

        // 2. Secondary fallback: DRM sysfs or nvidia-smi
        var (total, used, free) = GetGpuVramInfo(deviceId);
        if (total > 0 && free > 0)
        {
            return CalculateVramBudget(total, used, utilizationFraction, safetyMarginBytes);
        }

        // Safe fallback for systems where no GPU query succeeded (e.g. CPU): 4 GiB
        return 4L * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// Gets total, used, and free GPU VRAM in bytes for the specified device.
    /// Supports Vulkan VK_EXT_memory_budget (primary), Linux AMD/Intel DRM sysfs, and NVIDIA via nvidia-smi.
    /// Returns (0, 0, 0) if GPU memory cannot be detected.
    /// </summary>
    public static (long Total, long Used, long Free) GetGpuVramInfo(int deviceId = 0)
    {
        // 1. Primary: Vulkan VK_EXT_memory_budget
        var vkMem = VulkanMemoryProvider.QueryDevice(deviceId);
        if (vkMem != null && vkMem.TotalVramBytes > 0)
        {
            long free =
                vkMem.BudgetBytes > 0
                    ? vkMem.BudgetBytes
                    : Math.Max(0, vkMem.TotalVramBytes - vkMem.UsedVramBytes);
            return (vkMem.TotalVramBytes, vkMem.UsedVramBytes, free);
        }

        // 2. Fallback: Linux DRM sysfs (AMD and Intel)
        var drmCards = QueryDrmGpus();
        if (drmCards.Count > 0)
        {
            var selected =
                (deviceId >= 0 && deviceId < drmCards.Count)
                    ? drmCards[deviceId]
                    : drmCards.OrderByDescending(c => c.Total).First();

            long free = Math.Max(0, selected.Total - selected.Used);
            return (selected.Total, selected.Used, free);
        }

        // 3. Fallback: NVIDIA (nvidia-smi)
        var nvidiaInfo = TryGetNvidiaGpuMemory(deviceId);
        if (nvidiaInfo.HasValue && nvidiaInfo.Value.Total > 0)
        {
            long free = Math.Max(0, nvidiaInfo.Value.Total - nvidiaInfo.Value.Used);
            return (nvidiaInfo.Value.Total, nvidiaInfo.Value.Used, free);
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// Reads current VRAM and GTT used in bytes, or returns (0, 0) if unavailable.
    /// </summary>
    public static (long VramUsed, long GttUsed) GetGpuMemoryUsage(int deviceId = 0)
    {
        // Linux DRM sysfs has direct kernel-level mem_info_gtt_used tracking for AMD
        var drmCards = QueryDrmGpus();
        if (drmCards.Count > 0)
        {
            var selected =
                (deviceId >= 0 && deviceId < drmCards.Count)
                    ? drmCards[deviceId]
                    : drmCards.OrderByDescending(c => c.Total).First();

            return (selected.Used, selected.GttUsed);
        }

        // Vulkan memory tracking
        var vkMem = VulkanMemoryProvider.QueryDevice(deviceId);
        if (vkMem != null && vkMem.TotalVramBytes > 0)
        {
            return (vkMem.UsedVramBytes, vkMem.UsedGttBytes);
        }

        var nvidiaInfo = TryGetNvidiaGpuMemory(deviceId);
        if (nvidiaInfo.HasValue)
        {
            return (nvidiaInfo.Value.Used, 0);
        }

        return (0, 0);
    }

    private static List<(string CardPath, long Total, long Used, long GttUsed)> QueryDrmGpus()
    {
        var result = new List<(string CardPath, long Total, long Used, long GttUsed)>();
        try
        {
            if (!Directory.Exists("/sys/class/drm"))
            {
                return result;
            }

            var cardDirs = Directory
                .GetDirectories("/sys/class/drm", "card*")
                .Where(d => !Path.GetFileName(d).Contains('-'))
                .OrderBy(d => d)
                .ToList();

            foreach (var card in cardDirs)
            {
                string deviceDir = Path.Combine(card, "device");
                if (!Directory.Exists(deviceDir))
                {
                    continue;
                }

                long total = 0;
                long used = 0;
                long gtt = 0;

                // AMD GPU sysfs paths
                string amdTotalPath = Path.Combine(deviceDir, "mem_info_vram_total");
                string amdUsedPath = Path.Combine(deviceDir, "mem_info_vram_used");
                string amdGttPath = Path.Combine(deviceDir, "mem_info_gtt_used");

                if (
                    File.Exists(amdTotalPath)
                    && long.TryParse(
                        File.ReadAllText(amdTotalPath).Trim(),
                        CultureInfo.InvariantCulture,
                        out long amdTotal
                    )
                    && amdTotal > 0
                )
                {
                    total = amdTotal;
                    if (
                        File.Exists(amdUsedPath)
                        && long.TryParse(
                            File.ReadAllText(amdUsedPath).Trim(),
                            CultureInfo.InvariantCulture,
                            out long amdUsed
                        )
                    )
                    {
                        used = amdUsed;
                    }
                    if (
                        File.Exists(amdGttPath)
                        && long.TryParse(
                            File.ReadAllText(amdGttPath).Trim(),
                            CultureInfo.InvariantCulture,
                            out long amdGtt
                        )
                    )
                    {
                        gtt = amdGtt;
                    }
                }
                else
                {
                    // Intel Xe / i915 sysfs paths
                    string intelTile0Total = Path.Combine(
                        deviceDir,
                        "tile0",
                        "memory0",
                        "total_bytes"
                    );
                    string intelTile0Alloc = Path.Combine(
                        deviceDir,
                        "tile0",
                        "memory0",
                        "alloc_bytes"
                    );
                    string intelLmemTotal = Path.Combine(deviceDir, "lmem_total_bytes");
                    string intelLmemAlloc = Path.Combine(deviceDir, "lmem_alloc_bytes");

                    if (
                        File.Exists(intelTile0Total)
                        && long.TryParse(
                            File.ReadAllText(intelTile0Total).Trim(),
                            CultureInfo.InvariantCulture,
                            out long itTotal
                        )
                        && itTotal > 0
                    )
                    {
                        total = itTotal;
                        if (
                            File.Exists(intelTile0Alloc)
                            && long.TryParse(
                                File.ReadAllText(intelTile0Alloc).Trim(),
                                CultureInfo.InvariantCulture,
                                out long itAlloc
                            )
                        )
                        {
                            used = itAlloc;
                        }
                    }
                    else if (
                        File.Exists(intelLmemTotal)
                        && long.TryParse(
                            File.ReadAllText(intelLmemTotal).Trim(),
                            CultureInfo.InvariantCulture,
                            out long ilTotal
                        )
                        && ilTotal > 0
                    )
                    {
                        total = ilTotal;
                        if (
                            File.Exists(intelLmemAlloc)
                            && long.TryParse(
                                File.ReadAllText(intelLmemAlloc).Trim(),
                                CultureInfo.InvariantCulture,
                                out long ilAlloc
                            )
                        )
                        {
                            used = ilAlloc;
                        }
                    }
                }

                if (total > 0)
                {
                    result.Add((card, total, used, gtt));
                }
            }
        }
        catch
        {
            // Ignore DRM sysfs read errors
        }

        return result;
    }

    private static (long Total, long Used)? TryGetNvidiaGpuMemory(int deviceId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments =
                    $"--id={deviceId} --query-gpu=memory.total,memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            // Output format: "16384, 2560" (values in MiB)
            var parts = output.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            );
            if (
                parts.Length >= 2
                && long.TryParse(parts[0], CultureInfo.InvariantCulture, out long totalMib)
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long usedMib)
            )
            {
                return (totalMib * 1024L * 1024L, usedMib * 1024L * 1024L);
            }
        }
        catch
        {
            // nvidia-smi not available or failed
        }

        return null;
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

        // Determine uniform padded dimensions across all tiles in this image.
        // Passing uniform tensor shapes to ONNX Runtime (WebGPU / Dawn EP) prevents
        // per-tile buffer re-allocation and memory arena accumulation in Vulkan VRAM/GTT.
        int maxPaddedW = tileSizeX + 2 * overlap;
        int maxPaddedH = tileSizeY + 2 * overlap;
        int uniformTargetDim = Math.Max(maxPaddedW, maxPaddedH);
        int uniformTargetSize = ((uniformTargetDim + 63) / 64) * 64;

        if (tileCountX <= 1 && tileCountY <= 1)
        {
            return UpscaleTile(
                rgbBytes,
                width,
                height,
                scale,
                session,
                cancellationToken,
                targetW: uniformTargetSize,
                targetH: uniformTargetSize
            );
        }

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
