namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Version of the worker/server gRPC protocol, so version skew fails with a clear message instead of
/// an opaque <c>Unimplemented</c> for every task.
///
/// Bump <see cref="Current"/> on any incompatible wire change. When a build can still serve the
/// previous version, leave <see cref="MinSupported"/> behind it; raise <see cref="MinSupported"/> once
/// the older version is dropped. The handshake is bidirectional and range-based: each side sends its
/// <c>[MinSupported, Current]</c> range and validates the other's with <see cref="IsCompatible"/>,
/// which accepts any overlap; both then use <see cref="Negotiated"/> (the overlap's upper bound) as
/// the effective wire version.
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

    /// <summary>
    /// True when this build's range [<see cref="MinSupported"/>, <see cref="Current"/>] overlaps the
    /// peer's range [<paramref name="peerMin"/>, <paramref name="peerCurrent"/>], so the two can agree
    /// on a common version. Each side validates the other's whole range with this, and both then use
    /// <see cref="Negotiated"/> as the effective wire version.
    /// </summary>
    public static bool IsCompatible(int peerCurrent, int peerMin) =>
        Math.Max(MinSupported, peerMin) <= Math.Min(Current, peerCurrent);

    /// <summary>
    /// The effective wire version after a compatible handshake: the upper bound of the overlap. Both
    /// sides must use this rather than their own <see cref="Current"/> for any version-dependent
    /// behavior, so a newer peer does not send messages an older one cannot interpret. Today every
    /// build is [3, 3], so this is always <see cref="Current"/>.
    /// </summary>
    public static int Negotiated(int peerCurrent) => Math.Min(Current, peerCurrent);
}
