using System.Collections.Concurrent;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>A page of a streamed chapter, in source-archive order.</summary>
public sealed record SpoolPageDescriptor(int Index, string SourceName, string OutputName);

/// <summary>
/// Process-local spool of upscaled pages for page-streamed tasks. Pages are written atomically as
/// they arrive and the final CBZ is assembled from the source archive plus the spooled pages, so a
/// dropped connection only loses the in-flight pages and a re-dispatched task resumes at the first
/// missing page.
///
/// The spool is keyed by a content identity; a task re-dispatched with a different identity (source
/// or profile changed) has its spool discarded so old and new bytes are never mixed.
/// </summary>
public sealed class PageStreamSpool
{
    private readonly ConcurrentDictionary<int, PageStreamSession> _sessions = new();
    private readonly ILogger<PageStreamSpool> _logger;

    public PageStreamSpool(ILogger<PageStreamSpool> logger)
    {
        _logger = logger;
    }

    // Unique per PageStreamSpool instance (and therefore per process): two app instances, or two
    // test collections, on one host must not share task directories.
    public string SpoolRoot { get; } =
        Path.Combine(
            Path.GetTempPath(),
            "mangaingestwithupscaling",
            "page_spool",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}"
        );

    /// <summary>
    /// Returns the session for a task, creating it or resetting it when the identity changed.
    /// </summary>
    public PageStreamSession GetOrCreateSession(int taskId, string identity, int pageCount)
    {
        PageStreamSession session = _sessions.GetOrAdd(
            taskId,
            _ => new PageStreamSession(
                taskId,
                identity,
                pageCount,
                Path.Combine(SpoolRoot, taskId.ToString())
            )
        );

        lock (session.Gate)
        {
            if (session.Identity != identity)
            {
                _logger.LogInformation(
                    "Page spool identity changed for task {TaskId}; discarding {Count} spooled page(s).",
                    taskId,
                    session.Completed.Count
                );
                session.Reset(identity, pageCount, Path.Combine(SpoolRoot, taskId.ToString()));
            }
            else
            {
                session.PageCount = pageCount;
            }

            session.LastTouchedUtc = DateTime.UtcNow;
        }

        return session;
    }

    public IReadOnlyCollection<int> GetCompletedPages(PageStreamSession session)
    {
        lock (session.Gate)
        {
            return session.Completed.ToArray();
        }
    }

    /// <summary>
    /// Writes one page atomically and records it as completed. Idempotent: re-uploading a page
    /// replaces it.
    /// </summary>
    public async Task WritePageAsync(
        PageStreamSession session,
        int pageIndex,
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        lock (session.Gate)
        {
            Directory.CreateDirectory(session.Directory);
        }

        string path = session.PagePath(pageIndex);
        // Unique per write so two workers racing on the same page cannot corrupt each other's temp.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (
            FileStream output = new(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous
            )
        )
        {
            await content.CopyToAsync(output, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);

        lock (session.Gate)
        {
            session.FailedPages.Remove(pageIndex);
            session.Completed.Add(pageIndex);
            session.LastTouchedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records that a page could not be produced. The page counts as done (so the chapter can
    /// finish) and is copied through from the source archive during assembly.
    /// </summary>
    public void MarkPageFailed(PageStreamSession session, int pageIndex)
    {
        lock (session.Gate)
        {
            session.Completed.Remove(pageIndex);
            session.FailedPages.Add(pageIndex);
            session.LastTouchedUtc = DateTime.UtcNow;
        }
    }

    public bool IsComplete(PageStreamSession session)
    {
        lock (session.Gate)
        {
            return session.PageCount > 0
                && session.Completed.Count + session.FailedPages.Count >= session.PageCount;
        }
    }

    /// <summary>
    /// Builds the final CBZ at <paramref name="destinationPath"/> from the source archive: every
    /// non-image entry is copied unchanged and every image entry is replaced by its spooled page,
    /// written under the page's output name. Source order is preserved.
    /// </summary>
    public void Assemble(
        PageStreamSession session,
        string sourcePath,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    )
    {
        Dictionary<string, SpoolPageDescriptor> bySource = pages.ToDictionary(
            p => p.SourceName,
            StringComparer.Ordinal
        );
        HashSet<int> failed;
        lock (session.Gate)
        {
            failed = new HashSet<int>(session.FailedPages);
        }

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (destinationDirectory is not null)
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using ZipArchive source = ZipFile.OpenRead(sourcePath);
        using ZipArchive output = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        foreach (ZipArchiveEntry entry in source.Entries)
        {
            // Skip directory entries (zip stores them with an empty name).
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (
                bySource.TryGetValue(entry.FullName, out SpoolPageDescriptor? page)
                && !failed.Contains(page.Index)
            )
            {
                string pagePath = session.PagePath(page.Index);
                if (!File.Exists(pagePath))
                {
                    throw new FileNotFoundException(
                        $"Spooled page {page.Index} for task {session.TaskId} is missing.",
                        pagePath
                    );
                }

                ZipArchiveEntry outputEntry = output.CreateEntry(page.OutputName);
                using Stream input = File.OpenRead(pagePath);
                using Stream target = outputEntry.Open();
                input.CopyTo(target);
            }
            else
            {
                ZipArchiveEntry outputEntry = output.CreateEntry(entry.FullName);
                using Stream input = entry.Open();
                using Stream target = outputEntry.Open();
                input.CopyTo(target);
            }
        }
    }

    /// <summary>
    /// Builds a CBZ containing only the spooled pages, named by their output names. Used for
    /// repair, where the result is merged into the existing upscaled chapter rather than replacing
    /// it.
    /// </summary>
    public void AssemblePagesOnly(
        PageStreamSession session,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    )
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (destinationDirectory is not null)
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        HashSet<int> failed;
        lock (session.Gate)
        {
            failed = new HashSet<int>(session.FailedPages);
        }

        using ZipArchive output = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (SpoolPageDescriptor page in pages)
        {
            if (failed.Contains(page.Index))
            {
                continue;
            }

            string pagePath = session.PagePath(page.Index);
            if (!File.Exists(pagePath))
            {
                throw new FileNotFoundException(
                    $"Spooled page {page.Index} for task {session.TaskId} is missing.",
                    pagePath
                );
            }

            ZipArchiveEntry outputEntry = output.CreateEntry(page.OutputName);
            using Stream input = File.OpenRead(pagePath);
            using Stream target = outputEntry.Open();
            input.CopyTo(target);
        }
    }

    public void Remove(int taskId)
    {
        if (_sessions.TryRemove(taskId, out PageStreamSession? session))
        {
            session.DeleteDirectory(_logger);
        }
    }

    /// <summary>Deletes sessions idle longer than <paramref name="retention"/>.</summary>
    public void SweepStale(TimeSpan retention)
    {
        DateTime cutoff = DateTime.UtcNow - retention;
        foreach ((int taskId, PageStreamSession session) in _sessions)
        {
            bool stale;
            lock (session.Gate)
            {
                stale = session.LastTouchedUtc < cutoff;
            }

            if (stale && _sessions.TryRemove(taskId, out PageStreamSession? removed))
            {
                _logger.LogInformation("Removing stale page spool for task {TaskId}.", taskId);
                removed.DeleteDirectory(_logger);
            }
        }

        // Roots left by a previous process are invisible to _sessions (which is empty after a
        // restart), so sweep the shared parent as well, skipping this process's live root.
        try
        {
            if (Directory.Exists(SpoolParent))
            {
                foreach (string directory in Directory.EnumerateDirectories(SpoolParent))
                {
                    if (string.Equals(directory, SpoolRoot, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        {
                            Directory.Delete(directory, recursive: true);
                            _logger.LogInformation(
                                "Removed stale page spool root {Directory}.",
                                directory
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(
                            ex,
                            "Failed to sweep page spool root {Directory}.",
                            directory
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to sweep the page spool parent {Parent}.", SpoolParent);
        }
    }

    private static string SpoolParent =>
        Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling", "page_spool");
}

/// <summary>Mutable per-task spool state, guarded by <see cref="Gate"/>.</summary>
public sealed class PageStreamSession
{
    public PageStreamSession(int taskId, string identity, int pageCount, string directory)
    {
        TaskId = taskId;
        Identity = identity;
        PageCount = pageCount;
        Directory = directory;
    }

    public int TaskId { get; }
    public string Identity { get; private set; }
    public int PageCount { get; set; }
    public string Directory { get; private set; }
    public HashSet<int> Completed { get; } = new();

    /// <summary>Pages the worker reported as failed; they are copied through during assembly.</summary>
    public HashSet<int> FailedPages { get; } = new();
    public Lock Gate { get; } = new();
    public DateTime LastTouchedUtc { get; set; } = DateTime.UtcNow;

    public string PagePath(int pageIndex) => Path.Combine(Directory, $"page_{pageIndex:D5}.bin");

    public void Reset(string identity, int pageCount, string directory)
    {
        DeleteDirectory();
        Identity = identity;
        PageCount = pageCount;
        Directory = directory;
        Completed.Clear();
        FailedPages.Clear();
    }

    public void DeleteDirectory(ILogger? logger = null)
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete page spool directory {Directory}.", Directory);
        }
    }
}
