namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Version of the worker/server gRPC protocol, so version skew fails with a clear message instead of
/// an opaque <c>Unimplemented</c> for every task.
///
/// Bump <see cref="Current"/> on any incompatible wire change. When a build can still serve the
/// previous version, leave <see cref="MinSupported"/> behind it; raise <see cref="MinSupported"/> once
/// the older version is dropped. The handshake is bidirectional: the worker sends its
/// <see cref="Current"/> to <c>CheckConnection</c> and the server validates it against
/// <c>[MinSupported, Current]</c>, while the worker validates the server's <see cref="Current"/> the
/// same way. Both sides must accept the other's <see cref="Current"/> for the pair to interoperate.
/// </summary>
public static class UpscalingProtocolVersion
{
    /// <summary>The version this build speaks.</summary>
    public const int Current = 3;

    /// <summary>The oldest version this build can still interoperate with.</summary>
    public const int MinSupported = 3;

    /// <summary>True when <paramref name="peerVersion"/> is within this build's supported range.</summary>
    public static bool IsSupported(int peerVersion) =>
        peerVersion >= MinSupported && peerVersion <= Current;
}
