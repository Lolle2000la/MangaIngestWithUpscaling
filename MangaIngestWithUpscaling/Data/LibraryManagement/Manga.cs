namespace MangaIngestWithUpscaling.Data.LibraryManagement;

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

    public void ChangePrimaryTitle(string newTitle, bool addOldToAlternative)
    {
        if (addOldToAlternative
            && !OtherTitles.Any(t => t.Title == PrimaryTitle))
        {
            OtherTitles.Add(new MangaAlternativeTitle
            {
                Title = PrimaryTitle,
                Manga = this,
                MangaId = Id
            });
        }
        PrimaryTitle = newTitle;

        var titleToRemove = OtherTitles
            .FirstOrDefault(t => t.Title == newTitle);
        OtherTitles.Remove(titleToRemove);
    }

    public Manga Clone()
    {
        return (Manga)MemberwiseClone();
    }
}