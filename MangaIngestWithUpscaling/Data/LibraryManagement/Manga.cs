﻿namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a manga series, including its primary title,
/// alternative titles, author, and a reference to the Library it belongs to.
/// </summary>
public class Manga
{
    public int Id { get; set; }
    public string PrimaryTitle { get; set; }
    public string? Author { get; set; }
    public bool? ShouldUpscale { get; set; } = null;

    public int LibraryId { get; set; }
    public Library Library { get; set; }

    public List<MangaAlternativeTitle> OtherTitles { get; set; }
        = [];

    public List<Chapter> Chapters { get; set; } = [];
}