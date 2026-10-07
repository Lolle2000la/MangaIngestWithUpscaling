using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Services.GPU;

/// <summary>
/// Detects whether hardware-accelerated 16-bit floating point (FP16) inference is supported
/// by the target execution device and GPU backend.
/// </summary>
public static class Fp16CapabilityDetector
{
    /// <summary>
    /// Determines whether FP16 is supported on the given device and backend.
    /// Returns false if CPU is selected or if no capable GPU is available.
    /// </summary>
    /// <param name="deviceId">The 0-based accelerator device index.</param>
    /// <param name="preferredBackend">The configured GPU backend preference.</param>
    /// <param name="useCpu">Whether CPU execution is forced or selected.</param>
    /// <returns>True if FP16 inference is supported; otherwise, false.</returns>
    public static bool IsFp16Supported(
        int deviceId = 0,
        GpuBackend preferredBackend = GpuBackend.Auto,
        bool useCpu = false
    )
    {
        // 1. If CPU is selected, FP16 is not accelerated in ONNX Runtime (often slower via emulation)
        if (useCpu || preferredBackend == GpuBackend.CPU)
        {
            return false;
        }

        // 2. Primary: Vulkan check (native cross-platform support across AMD, NVIDIA, Intel, Apple)
        if (VulkanMemoryProvider.IsAvailable)
        {
            if (VulkanMemoryProvider.IsCpuDevice(deviceId))
            {
                return false;
            }

            return VulkanMemoryProvider.SupportsFp16(deviceId);
        }

        // 3. Fallback when Vulkan is unavailable (e.g. pure CUDA container, headless setup)
        var (total, _, _) = OnnxTiler.GetGpuVramInfo(deviceId);
        if (total > 0)
        {
            return true;
        }

        // If no GPU could be detected at all, inference will fall back to CPU
        return false;
    }
}
