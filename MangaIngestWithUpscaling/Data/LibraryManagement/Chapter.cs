﻿using System.ComponentModel.DataAnnotations.Schema;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a chapter in a manga series, including file name,
/// relative path, and optional reference to an UpscalerConfig.
/// </summary>
public class Chapter
{
    public int Id { get; set; }
    public int MangaId { get; set; }
    public Manga Manga { get; set; }

    public string FileName { get; set; }
    public string RelativePath { get; set; }

    public bool IsUpscaled { get; set; }
    public int? UpscalerProfileId { get; set; }
    public UpscalerProfile UpscalerProfile { get; set; }

    [NotMapped]
    public string NotUpscaledFullPath => Path.Combine(Manga.Library.NotUpscaledLibraryPath, RelativePath);

    [NotMapped]
    public string? UpscaledFullPath => UpscalerProfileId != null
        ? Path.Combine(Manga.Library.UpscaledLibraryPath!, RelativePath)
        : null;
}