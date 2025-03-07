﻿namespace MangaIngestWithUpscaling.Data.LibraryManagement;

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

    public bool UpscaleOnIngest { get; set; }
    public int? UpscalerProfileId { get; set; }
    public UpscalerProfile? UpscalerProfile { get; set; }

    public List<Manga> MangaSeries { get; set; } = [];
    public List<LibraryFilterRule> FilterRules { get; set; } = [];
}