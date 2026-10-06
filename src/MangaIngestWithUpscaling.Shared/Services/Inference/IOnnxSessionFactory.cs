using Microsoft.ML.OnnxRuntime;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// Creates and caches <see cref="InferenceSession"/> instances configured with optimal
/// execution providers (MIGraphX for AMD on Linux, DirectML on Windows, CUDA, or CPU fallback).
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
}
