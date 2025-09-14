using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

public interface ILibraryIntegrityChecker
{
    /// <summary>
    /// Checks the integrity of a library. Will check all of its manga series and associated chapters one-by-one.
    /// </summary>
    /// <param name="library">The library to check.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Whether the check has resulted in changes. If <c>true</c> you should reload your data.</returns>
    Task<bool> CheckIntegrity(Library library, CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Checks the integrity of a library with progress reporting.
    /// </summary>
    /// <param name="library">The library to check.</param>
    /// <param name="progress">Progress reporter (units: chapters).</param>
    /// <param name="cancellationToken"></param>
    Task<bool> CheckIntegrity(Library library, IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null);

    /// <summary>
    /// Checks the integrity of a manga series. Will check all of its chapters one-by-one.
    /// </summary>
    /// <param name="manga">The manga series to check.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Whether the check has resulted in changes. If <c>true</c> you should reload your data.</returns>
    Task<bool> CheckIntegrity(Manga manga, CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Checks the integrity of a manga series with progress reporting.
    /// </summary>
    /// <param name="manga">The manga series to check.</param>
    /// <param name="progress">Progress reporter (units: chapters).</param>
    /// <param name="cancellationToken"></param>
    Task<bool> CheckIntegrity(Manga manga, IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null);

    /// <summary>
    /// Checks the integrity of a chapter.
    /// </summary>
    /// <param name="chapter">The chapter to check.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Whether the check has resulted in changes. If <c>true</c> you should reload your data.</returns>
    Task<bool> CheckIntegrity(Chapter chapter, CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Checks the integrity of a chapter with progress reporting.
    /// </summary>
    /// <param name="chapter">The chapter to check.</param>
    /// <param name="progress">Progress reporter (units: chapters, current: chapter index).</param>
    /// <param name="cancellationToken"></param>
    Task<bool> CheckIntegrity(Chapter chapter, IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null);

    /// <summary>
    /// Checks the integrity of all libraries.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Whether the check has resulted in changes. If <c>true</c> you should reload your data.</returns>
    Task<bool> CheckIntegrity(CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Checks the integrity of all libraries with progress reporting.
    /// </summary>
    /// <param name="progress">Progress reporter (units: chapters).</param>
    /// <param name="cancellationToken"></param>
    Task<bool> CheckIntegrity(IProgress<IntegrityProgress> progress, CancellationToken? cancellationToken = null);
}