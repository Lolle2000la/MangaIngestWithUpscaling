namespace MangaIngestWithUpscaling.Shared.Data.Abstractions;

/// <summary>
/// Marks an entity whose <see cref="ModifiedAt"/> is updated automatically whenever it is inserted
/// or modified.
/// </summary>
public interface IHasModifiedAt
{
    DateTime ModifiedAt { get; set; }
}
