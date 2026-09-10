namespace MangaIngestWithUpscaling.Shared.Data.Abstractions;

/// <summary>
/// Marks an entity whose <see cref="CreatedAt"/> is set automatically when it is inserted.
/// </summary>
public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}
