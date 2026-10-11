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
    /// <para>
    /// The disposal is deferred to a background drain that waits for every inference scope to end,
    /// so it is safe to call from inside a scope but the memory is not necessarily free when this
    /// returns. See <see cref="ReleaseCachedSessions"/> for a synchronous alternative.
    /// </para>
    /// </summary>
    void InvalidateSession(string modelPath);

    /// <summary>
    /// Invalidates and disposes all currently cached sessions, releasing GPU VRAM back to the driver.
    /// <para>
    /// The disposal is deferred to a background drain that waits for every inference scope to end,
    /// so it is safe to call from inside a scope but the memory is not necessarily free when this
    /// returns. A caller that provably holds no scope and needs the memory gone before its next
    /// step — an out-of-memory retry, a chapter boundary — must use
    /// <see cref="ReleaseCachedSessions"/> instead.
    /// </para>
    /// </summary>
    void InvalidateAllSessions();

    /// <summary>
    /// Retires every cached session and disposes it synchronously before returning, so the device
    /// memory is actually free for the caller's next step.
    /// <para>
    /// Only safe from a context that holds no inference scope: the implementation takes the write
    /// lock on the calling thread, and calling it under a read scope would deadlock (or throw
    /// <see cref="LockRecursionException"/>). A caller that holds a scope must use
    /// <see cref="InvalidateSession"/>/<see cref="InvalidateAllSessions"/> and let the background
    /// drain free the sessions once every scope has ended.
    /// </para>
    /// <para>
    /// The default implementation has no synchronous path available to it and simply defers to
    /// <see cref="InvalidateAllSessions"/>; test doubles and alternative factories do not have to
    /// implement it.
    /// </para>
    /// </summary>
    void ReleaseCachedSessions() => InvalidateAllSessions();

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
