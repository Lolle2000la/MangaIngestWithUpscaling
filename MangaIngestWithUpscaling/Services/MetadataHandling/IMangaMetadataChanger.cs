using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

/// <summary>
/// Interface for changing metadata of a manga series.
/// </summary>
public interface IMangaMetadataChanger
{
    /// <summary>
    /// Changes the primary title of a manga series.
    /// This also moves all the files into new directories and changes the metadata in the chapter files accordingly.
    /// </summary>
    /// <param name="manga">The manga of which to change the title.</param>
    /// <param name="newTitle">The new title to change the manga title to.</param>
    /// <param name="addOldToAlternative">
    /// If <c>true</c>, this will add the old title to the list of other titles.
    /// This is useful to recognize ingested chapters of the same series with different titles (i.e english and japanese).
    /// </param>
    /// <exception cref="TitleAlreadyUsedException">Indicates that the title has already been used.</exception>
    /// <returns></returns>
    Task ChangeTitle(Manga manga, string newTitle, bool addOldToAlternative = true);
    /// <summary>
    /// Updates the title of a upscaled chapter file and moves it to the correct directory.
    /// </summary>
    /// <param name="chapter"></param>
    /// <param name="newTitle"></param>
    /// <param name="origChapterPath"></param>
    /// <remarks>
    /// This is mainly used outside of <see cref="MangaMetadataChanger"/> to ensure that the Metadata changes are applied to the currently upscaled chapters as well.
    /// If the upscaling takes too long, then the metadata might be stale and not reflect the current state of the manga series.
    /// </remarks>
    void ApplyMangaTitleToUpscaled(Chapter chapter, string newTitle, string origChapterPath);
    /// <summary>
    /// Changes the title of a chapter in the ComicInfo.xml metadata file.
    /// </summary>
    /// <param name="chapter">The chapter whose metadata to change.</param>
    /// <param name="newTitle">The new title to apply.</param>
    Task ChangeChapterTitle(Chapter chapter, string newTitle);
}
