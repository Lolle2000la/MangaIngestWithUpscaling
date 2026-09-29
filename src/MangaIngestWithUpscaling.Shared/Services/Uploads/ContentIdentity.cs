using System.Security.Cryptography;

namespace MangaIngestWithUpscaling.Shared.Services.Uploads;

/// <summary>
/// Opaque identity for a resumable upload. The worker hashes the file it is about to send and
/// passes the resulting string to the server, which uses it to decide whether chunks stored from a
/// previous attempt belong to the same bytes and verifies it again against the assembled result.
/// </summary>
public static class ContentIdentity
{
    public const string Sha256Prefix = "sha256:";

    /// <summary>Formats a SHA-256 digest as the identity string sent over the wire.</summary>
    public static string FromSha256(ReadOnlySpan<byte> hash) =>
        Sha256Prefix + Convert.ToHexStringLower(hash);
}
