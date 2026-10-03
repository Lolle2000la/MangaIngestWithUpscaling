namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Version of the worker/server gRPC protocol, so version skew fails with a clear message instead of
/// an opaque <c>Unimplemented</c> for every task.
///
/// Bump <see cref="Current"/> on any incompatible wire change. When a build can still serve the
/// previous version, leave <see cref="MinSupported"/> behind it; raise <see cref="MinSupported"/> once
/// the older version is dropped. The handshake is bidirectional: each side sends its
/// <c>[MinSupported, Current]</c> range and validates the other's with <see cref="IsCompatible"/>,
/// which requires the peer to speak this build's <c>Current</c> (no negotiated version exists yet).
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
    /// True when the peer can speak this build's <see cref="Current"/> protocol. Until the two sides
    /// negotiate a shared version (each side currently keeps using its own <c>Current</c>), a peer
    /// whose <c>Current</c> is newer is rejected: accepting it would let it send messages this build
    /// cannot interpret. A peer whose range spans our <c>Current</c> is accepted.
    /// </summary>
    public static bool IsCompatible(int peerCurrent, int peerMin) =>
        peerCurrent == Current && peerMin <= Current;
}
