namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Version of the worker/server gRPC protocol, so version skew fails with a clear message instead of
/// an opaque <c>Unimplemented</c> for every task.
///
/// Bump <see cref="Current"/> on any incompatible wire change. When a build can still serve the
/// previous version, leave <see cref="MinSupported"/> behind it; raise <see cref="MinSupported"/> once
/// the older version is dropped. A worker and server interoperate when each side's
/// <see cref="Current"/> falls within the other's <c>[MinSupported, Current]</c> range.
/// </summary>
public static class UpscalingProtocolVersion
{
    /// <summary>The version this build speaks.</summary>
    public const int Current = 2;

    /// <summary>The oldest version this build can still interoperate with.</summary>
    public const int MinSupported = 2;

    /// <summary>True when <paramref name="peerVersion"/> is within this build's supported range.</summary>
    public static bool IsSupported(int peerVersion) =>
        peerVersion >= MinSupported && peerVersion <= Current;
}
