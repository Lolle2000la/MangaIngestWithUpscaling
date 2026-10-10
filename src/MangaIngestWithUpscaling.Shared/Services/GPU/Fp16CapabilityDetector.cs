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
    /// Determines whether hardware-accelerated 16-bit floating point (FP16) inference is supported
    /// for the device that will actually run the model.
    /// </summary>
    /// <param name="resolvedBackend">
    /// The execution provider in use, already resolved (from <c>IOnnxSessionFactory.GetEffectiveBackend()</c>).
    /// Passing the resolved provider rather than the configured preference is what makes the answer
    /// about the device that will run, not about whichever device a preference happens to name.
    /// </param>
    /// <param name="acceleratorIndex">
    /// The 0-based accelerator device index, as the inference runtime counts accelerators — not a
    /// Vulkan enumeration index. See <see cref="VulkanMemoryProvider.ResolveGpuIndex"/>.
    /// </param>
    /// <param name="forceCpu">Whether CPU execution is forced or selected.</param>
    /// <returns>True if FP16 inference is supported; otherwise, false.</returns>
    public static bool IsFp16Supported(
        GpuBackend resolvedBackend,
        int acceleratorIndex = 0,
        bool forceCpu = false
    )
    {
        // 1. If CPU is selected, FP16 is not accelerated in ONNX Runtime (often slower via emulation)
        if (forceCpu || resolvedBackend == GpuBackend.CPU)
        {
            return false;
        }

        // 2. fp16 is native to every provider except WebGPU, so for those there is nothing to probe:
        //    the answer is a property of the provider, not of the device. Probing Vulkan instead
        //    would answer a question the provider is not going to ask — on a host with an NVIDIA
        //    dGPU and an Intel iGPU that reports no fp16, the whole set of downloads would change.
        if (resolvedBackend is not (GpuBackend.WebGPU or GpuBackend.Auto))
        {
            return true;
        }

        // 3. WebGPU runs on Vulkan wherever it is not Direct3D, so the device's own feature set
        //    decides. SupportsFp16 takes the accelerator index, not the Vulkan one.
        if (VulkanMemoryProvider.IsAvailable)
        {
            return VulkanMemoryProvider.SupportsFp16(acceleratorIndex);
        }

        // 4. Fallback when Vulkan is unavailable (e.g. pure CUDA container, headless setup)
        var (total, _, _) = OnnxTiler.GetGpuVramInfo(acceleratorIndex);
        if (total > 0)
        {
            return true;
        }

        // If no GPU could be detected at all, inference will fall back to CPU
        return false;
    }
}
