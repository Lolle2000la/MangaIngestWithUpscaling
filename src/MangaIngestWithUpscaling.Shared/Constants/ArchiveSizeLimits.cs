namespace MangaIngestWithUpscaling.Shared.Constants;

/// <summary>
/// Decompression ceilings shared by every path that reads a CBZ, so the whole-CBZ and page-streamed
/// paths bound a crafted archive the same way instead of each restating the numbers.
/// </summary>
public static class ArchiveSizeLimits
{
    /// <summary>
    /// Upper bound on the bytes decompressed out of a single archive entry (512 MiB), applied by the
    /// local whole-CBZ reader, the streamed per-page fetch and upload, and <c>ImageController</c>'s
    /// page endpoint. Fixed: no configuration raises or lowers it.
    /// </summary>
    public const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Default ceiling on the total decompressed bytes of one archive/task (8 GiB). The local
    /// whole-CBZ path applies it as a fixed per-archive bound; the page-streamed spool uses it as the
    /// default for the operator-settable <c>UpscalerConfig.MaxSpoolBytesPerTask</c>, so the two agree
    /// until an operator changes the spool value.
    /// </summary>
    public const long DefaultMaxArchiveBytes = 8L * 1024 * 1024 * 1024;
}
