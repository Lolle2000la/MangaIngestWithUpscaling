namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

/// <summary>
///     Progress payload for integrity checks. Mirrors UpscaleProgress shape for consistency.
/// </summary>
public sealed record IntegrityProgress(
    int? Total,
    int? Current,
    string? Scope,
    string? StatusMessage
);
