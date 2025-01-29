﻿namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Stores alternative titles for a manga.
/// </summary>
public class MangaAlternativeTitle
{
    public int Id { get; set; }
    public string Title { get; set; }

    public int MangaId { get; set; }
    public Manga Manga { get; set; }
}