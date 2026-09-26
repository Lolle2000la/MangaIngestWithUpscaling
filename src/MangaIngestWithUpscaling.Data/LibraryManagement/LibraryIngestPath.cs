namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// One directory that is watched and scanned for new chapters of a <see cref="Library"/>.
/// A library may have several ingest paths, for example when sources are mounted at different locations.
/// </summary>
public class LibraryIngestPath : ILibraryConfiguration
{
    public int Id { get; set; }

    public int LibraryId { get; set; }

    public Library Library { get; set; } = null!;

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Relative order of the ingest paths. Only used to present them in a stable order; it does not
    /// influence how chapters are ingested.
    /// </summary>
    public int SortOrder { get; set; }
}
