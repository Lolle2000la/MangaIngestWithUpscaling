namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Version of the worker/server gRPC protocol, so version skew fails with a clear message instead of
/// an opaque <c>Unimplemented</c> for every task.
///
/// Bump <see cref="Current"/> on any incompatible wire change. When a build can still serve the
/// previous version, leave <see cref="MinSupported"/> behind it; raise <see cref="MinSupported"/> once
/// the older version is dropped. The handshake is bidirectional and range-based: each side sends its
/// <c>[MinSupported, Current]</c> range and validates the other's with <see cref="IsCompatible"/>,
/// which accepts any overlap.
///
/// Version negotiation is <b>not implemented yet</b>: no behavior currently depends on the protocol
/// version, so both sides keep speaking their own <see cref="Current"/> and only the handshake's range
/// check guards compatibility. Any future version-dependent behavior must first carry the overlap's
/// upper bound on the wire and use it on both sides; otherwise a newer peer could emit a field or RPC
/// an older one cannot interpret.
/// </summary>
public static class UpscalingProtocolVersion
{
    /// <summary>The version this build speaks.</summary>
    public const int Current = 3;

    /// <summary>The oldest version this build can still interoperate with.</summary>
    public const int MinSupported = 3;

    /// <summary>
    /// True when this build's range [<see cref="MinSupported"/>, <see cref="Current"/>] overlaps the
    /// peer's range [<paramref name="peerMin"/>, <paramref name="peerCurrent"/>], so the two could
    /// agree on a common version. Each side validates the other's whole range with this; because
    /// negotiation is not implemented (see the class remarks), the accepted version is still each
    /// side's own <see cref="Current"/>.
    /// </summary>
    public static bool IsCompatible(int peerCurrent, int peerMin) =>
        Math.Max(MinSupported, peerMin) <= Math.Min(Current, peerCurrent);
}
