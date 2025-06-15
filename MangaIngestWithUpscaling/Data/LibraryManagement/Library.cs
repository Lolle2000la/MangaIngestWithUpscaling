using System.ComponentModel.DataAnnotations.Schema;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a library with paths for ingesting, storing not-upscaled,
/// and storing upscaled manga, plus optional filter rules.
/// </summary>
public class Library
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string IngestPath { get; set; }
    public string NotUpscaledLibraryPath { get; set; }
    public string? UpscaledLibraryPath { get; set; }
    public KavitaLibraryConfig KavitaConfig { get; set; } = new KavitaLibraryConfig();

    public bool UpscaleOnIngest { get; set; }
    public int? UpscalerProfileId { get; set; }
    public UpscalerProfile? UpscalerProfile { get; set; }

    public List<Manga> MangaSeries { get; set; } = [];
    public List<LibraryFilterRule> FilterRules { get; set; } = [];
    public List<LibraryRenameRule> RenameRules { get; set; } = new List<LibraryRenameRule>();

    public override bool Equals(object obj)
    {
        if (obj == null || GetType() != obj.GetType())
        {
            return false;
        }

        Library other = (Library)obj;

        return Id == other.Id &&
               Name == other.Name &&
               IngestPath == other.IngestPath &&
               NotUpscaledLibraryPath == other.NotUpscaledLibraryPath &&
               UpscaledLibraryPath == other.UpscaledLibraryPath &&
               KavitaConfig == other.KavitaConfig &&
               UpscaleOnIngest == other.UpscaleOnIngest &&
               UpscalerProfileId == other.UpscalerProfileId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Name, IngestPath, NotUpscaledLibraryPath, UpscaledLibraryPath, KavitaConfig, UpscaleOnIngest, UpscalerProfile);
    }
}

[ComplexType]
public record KavitaLibraryConfig
{
    /// <summary>
    /// The mount point for the folder with the originals in Kavita.
    /// This may differ from the normal path due to the usage of different mount points across containers.
    /// </summary>
    public string? NotUpscaledMountPoint { get; set; }
    /// <summary>
    /// The mount point for the folder with the upscaled images in Kavita.
    /// This may differ from the normal path due to the usage of different mount points across containers.
    /// </summary>
    public string? UpscaledMountPoint { get; set; }
}