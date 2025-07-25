﻿namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Stores alternative titles for a manga.
/// </summary>
public class MangaAlternativeTitle
{
    public string Title { get; set; } = string.Empty;

    public int MangaId { get; set; }
    public Manga Manga { get; set; } = default!;
}