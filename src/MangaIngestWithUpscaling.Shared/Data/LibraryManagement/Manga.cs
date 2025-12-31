using System.ComponentModel.DataAnnotations.Schema;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a manga series, including its primary title,
/// alternative titles, author, and a reference to the Library it belongs to.
/// </summary>
public class Manga
{
    public int Id { get; set; }
    public string PrimaryTitle { get; set; } = string.Empty;
    public string? Author { get; set; }
    public bool? ShouldUpscale { get; set; } = null;
    public bool? MergeChapterParts { get; set; } = null;

    public int LibraryId { get; set; }
    public Library Library { get; set; } = default!;

    public List<MangaAlternativeTitle> OtherTitles { get; set; } = [];

    public List<Chapter> Chapters { get; set; } = [];

    public int? UpscalerProfilePreferenceId { get; set; }

    public UpscalerProfile? UpscalerProfilePreference { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public UpscalerProfile? EffectiveUpscalerProfile
    {
        get
        {
            // If the manga has a specific upscaler profile, return it
            if (UpscalerProfilePreference != null)
            {
                return UpscalerProfilePreference;
            }

            // Otherwise, return the library's default upscaler profile
            return Library.UpscalerProfile;
        }
    }

    public void ChangePrimaryTitle(string newTitle, bool addOldToAlternative)
    {
        if (addOldToAlternative && !OtherTitles.Any(t => t.Title == PrimaryTitle))
        {
            OtherTitles.Add(
                new MangaAlternativeTitle
                {
                    Title = PrimaryTitle,
                    Manga = this,
                    MangaId = Id,
                }
            );
        }

        PrimaryTitle = newTitle;

        var titleToRemove = OtherTitles.FirstOrDefault(t => t.Title == newTitle);
        if (titleToRemove != null)
        {
            OtherTitles.Remove(titleToRemove);
        }
    }

    public Manga Clone()
    {
        return (Manga)MemberwiseClone();
    }
}
