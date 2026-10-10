using Microsoft.ML.OnnxRuntime;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// Creates and caches <see cref="InferenceSession"/> instances configured with optimal
/// execution providers (CUDA for NVIDIA, WebGPU for AMD/Intel/macOS/Windows, DirectML on Windows, or CPU fallback).
/// </summary>
public interface IOnnxSessionFactory : IDisposable
{
    /// <summary>
    /// Gets a cached session for the specified model path, or creates and caches a new one if not present.
    /// </summary>
    InferenceSession GetOrCreateSession(string modelPath);

    /// <summary>
    /// Creates a new un-cached <see cref="InferenceSession"/> for the specified model path.
    /// </summary>
    InferenceSession CreateSession(string modelPath);

    /// <summary>
    /// Invalidates and disposes the cached session for the specified model path if present.
    /// </summary>
    void InvalidateSession(string modelPath);

    /// <summary>
    /// Invalidates and disposes all currently cached sessions, releasing GPU VRAM back to the driver.
    /// </summary>
    void InvalidateAllSessions();

    /// <summary>
    /// Resolves and returns the effective hardware accelerator backend (e.g. CUDA, WebGPU, or CPU)
    /// based on configuration and platform availability.
    /// </summary>
    Shared.Configuration.GpuBackend GetEffectiveBackend();

    /// <summary>
    /// Enters a scoped execution block during which active model sessions cannot be disposed by
    /// eviction or session invalidation. Multiple inference scopes may run concurrently.
    /// </summary>
    IDisposable EnterInferenceScope() => NullInferenceScope.Instance;

    private sealed class NullInferenceScope : IDisposable
    {
        public static readonly NullInferenceScope Instance = new();

        public void Dispose() { }
    }
}
