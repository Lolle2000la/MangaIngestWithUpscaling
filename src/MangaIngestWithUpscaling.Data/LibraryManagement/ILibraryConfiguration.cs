namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Marks a library configuration entity (ingest paths, filter rules, rename rules) whose changes
/// should update the owning library's <see cref="Library.ModifiedAt"/>.
/// </summary>
public interface ILibraryConfiguration
{
    int LibraryId { get; set; }

    Library Library { get; set; }
}
