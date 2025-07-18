using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Microsoft.Extensions.Logging;
using MangaIngestWithUpscaling.Shared.Configuration;
using System.Runtime.InteropServices;

namespace MangaIngestWithUpscaling.Shared.Services.GPU;

public interface IGpuDetectionService
{
    Task<GpuBackend> DetectOptimalBackendAsync();
    Task<GpuInfo> GetGpuInfoAsync();
}

public record GpuInfo(
    string Vendor,
    string Renderer,
    string Version,
    GpuBackend RecommendedBackend
);

[RegisterScoped]
public class GpuDetectionService(ILogger<GpuDetectionService> logger) : IGpuDetectionService
{
    public async Task<GpuBackend> DetectOptimalBackendAsync()
    {
        try
        {
            var gpuInfo = await GetGpuInfoAsync();
            logger.LogInformation("Detected GPU: {Vendor} - {Renderer}, recommended backend: {Backend}", 
                gpuInfo.Vendor, gpuInfo.Renderer, gpuInfo.RecommendedBackend);
            return gpuInfo.RecommendedBackend;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to detect GPU using OpenGL, falling back to CPU backend");
            return GpuBackend.CPU;
        }
    }

    public async Task<GpuInfo> GetGpuInfoAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                // Create an offscreen window for OpenGL context
                var options = WindowOptions.Default;
                options.IsVisible = false;
                options.Size = new Silk.NET.Maths.Vector2D<int>(1, 1);
                options.Title = "GPU Detection";
                
                using var window = Window.Create(options);
                window.Initialize();

                var gl = GL.GetApi(window);
                
                // Get OpenGL strings and convert from unsafe pointers to managed strings
                var vendor = GetOpenGLString(gl, StringName.Vendor);
                var renderer = GetOpenGLString(gl, StringName.Renderer);
                var version = GetOpenGLString(gl, StringName.Version);

                logger.LogDebug("OpenGL Vendor: {Vendor}", vendor);
                logger.LogDebug("OpenGL Renderer: {Renderer}", renderer);
                logger.LogDebug("OpenGL Version: {Version}", version);

                var recommendedBackend = DetermineBackendFromVendor(vendor, renderer);
                
                return new GpuInfo(vendor, renderer, version, recommendedBackend);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during OpenGL GPU detection");
                throw;
            }
        });
    }

    private unsafe string GetOpenGLString(GL gl, StringName stringName)
    {
        try
        {
            var ptr = gl.GetString(stringName);
            return Marshal.PtrToStringAnsi((IntPtr)ptr) ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private GpuBackend DetermineBackendFromVendor(string vendor, string renderer)
    {
        var vendorLower = vendor?.ToLowerInvariant() ?? "";
        var rendererLower = renderer?.ToLowerInvariant() ?? "";

        // Check for NVIDIA
        if (vendorLower.Contains("nvidia") || 
            rendererLower.Contains("nvidia") || 
            rendererLower.Contains("geforce") ||
            rendererLower.Contains("quadro") ||
            rendererLower.Contains("tesla"))
        {
            logger.LogInformation("NVIDIA GPU detected via OpenGL");
            return GpuBackend.CUDA;
        }

        // Check for AMD
        if (vendorLower.Contains("amd") || 
            vendorLower.Contains("ati") ||
            rendererLower.Contains("radeon") ||
            rendererLower.Contains("amd") ||
            rendererLower.Contains("ati"))
        {
            logger.LogInformation("AMD GPU detected via OpenGL");
            return GpuBackend.ROCm;
        }

        // Check for Intel (typically CPU-only for ML workloads)
        if (vendorLower.Contains("intel"))
        {
            logger.LogInformation("Intel GPU detected via OpenGL, using CPU backend for ML");
            return GpuBackend.CPU;
        }

        logger.LogInformation("Unknown GPU vendor '{Vendor}', falling back to CPU backend", vendor);
        return GpuBackend.CPU;
    }
}
