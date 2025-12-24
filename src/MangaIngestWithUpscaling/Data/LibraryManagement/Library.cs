using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a library with paths for ingesting, storing not-upscaled,
/// and storing upscaled manga, plus optional filter rules.
/// </summary>
public class Library
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IngestPath { get; set; } = string.Empty;
    public string NotUpscaledLibraryPath { get; set; } = string.Empty;
    public string? UpscaledLibraryPath { get; set; }
    public KavitaLibraryConfig KavitaConfig { get; set; } = new KavitaLibraryConfig();

    public bool UpscaleOnIngest { get; set; }
    public int? UpscalerProfileId { get; set; }
    public UpscalerProfile? UpscalerProfile { get; set; }

    public bool MergeChapterParts { get; set; }

    public StripDetectionMode StripDetectionMode { get; set; }

    public List<Manga> MangaSeries { get; set; } = [];
    public List<LibraryFilterRule> FilterRules { get; set; } = [];
    public List<FilteredImage> FilteredImages { get; set; } = [];

    public ObservableCollection<LibraryRenameRule> RenameRules { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public override bool Equals(object? obj)
    {
        if (obj == null || GetType() != obj.GetType())
        {
            return false;
        }

        Library other = (Library)obj;

        return Id == other.Id
            && Name == other.Name
            && IngestPath == other.IngestPath
            && NotUpscaledLibraryPath == other.NotUpscaledLibraryPath
            && UpscaledLibraryPath == other.UpscaledLibraryPath
            && KavitaConfig == other.KavitaConfig
            && UpscaleOnIngest == other.UpscaleOnIngest
            && MergeChapterParts == other.MergeChapterParts
            && UpscalerProfileId == other.UpscalerProfileId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Id,
            Name,
            IngestPath,
            NotUpscaledLibraryPath,
            UpscaledLibraryPath,
            KavitaConfig,
            UpscaleOnIngest,
            HashCode.Combine(MergeChapterParts, UpscalerProfile)
        );
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
