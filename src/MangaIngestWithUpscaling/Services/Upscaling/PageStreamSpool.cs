using System.Collections.Concurrent;
using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Constants;

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
        FileStream output = BeginPageWrite(session, pageIndex, out string temp);
        await using (output)
        {
            await content.CopyToAsync(output, cancellationToken);
        }

        CommitPage(session, pageIndex, temp);
    }

    /// <summary>
    /// Opens a unique temp file for a page so a caller can stream bytes straight to disk instead of
    /// buffering the whole page in memory. The page is only recorded as done once
    /// <see cref="CommitPage"/> moves the temp file into place.
    /// </summary>
    public FileStream BeginPageWrite(PageStreamSession session, int pageIndex, out string tempPath)
    {
        lock (session.Gate)
        {
            Directory.CreateDirectory(session.Directory);
        }

        string path = session.PagePath(pageIndex);
        // Unique per write so two workers racing on the same page cannot corrupt each other's temp.
        tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        return new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous
        );
    }

    /// <summary>
    /// Atomically moves a completed page into place and records it as done, but only while the
    /// session still holds <paramref name="expectedIdentity"/>. A session whose identity was reset
    /// by a concurrent manifest is stale, so the page is dropped instead of being mixed into the
    /// new identity. Returns <c>false</c> when the page was not committed.
    /// </summary>
    public bool TryCommitPage(
        PageStreamSession session,
        string expectedIdentity,
        int pageIndex,
        string tempPath,
        long size = 0
    )
    {
        lock (session.Gate)
        {
            if (!string.Equals(session.Identity, expectedIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            File.Move(tempPath, session.PagePath(pageIndex), overwrite: true);
            session.Completed.Add(pageIndex);
            // Re-uploading a page replaces its bytes; adjust by the delta so legitimate retries do
            // not spuriously trip the per-task budget.
            long previous = session.PageSizes.TryGetValue(pageIndex, out long prev) ? prev : 0;
            session.PageSizes[pageIndex] = size;
            session.TotalBytes += size - previous;
            session.LastTouchedUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>Atomically moves a completed page into place and records it as done.</summary>
    public void CommitPage(PageStreamSession session, int pageIndex, string tempPath) =>
        TryCommitPage(
            session,
            session.Identity,
            pageIndex,
            tempPath,
            new FileInfo(tempPath).Length
        );

    /// <summary>Bytes committed to the session so far, for the per-task spool budget.</summary>
    public long GetTotalBytes(PageStreamSession session)
    {
        lock (session.Gate)
        {
            return session.TotalBytes;
        }
    }

    public bool IsComplete(PageStreamSession session)
    {
        lock (session.Gate)
        {
            return session.PageCount > 0 && session.Completed.Count >= session.PageCount;
        }
    }

    /// <summary>
    /// Claims the one-shot finalize for a session, so two workers that both see the chapter as
    /// complete cannot assemble (or finalize detection) concurrently. Also refuses when the
    /// session's identity no longer matches the caller's, so a stale worker cannot finalize the
    /// new identity's chapter.
    /// </summary>
    public bool TryBeginAssembly(PageStreamSession session, string expectedIdentity)
    {
        lock (session.Gate)
        {
            if (
                session.Assembling
                || !string.Equals(session.Identity, expectedIdentity, StringComparison.Ordinal)
            )
            {
                return false;
            }

            session.Assembling = true;
            return true;
        }
    }

    public bool TryBeginAssembly(PageStreamSession session) =>
        TryBeginAssembly(session, session.Identity);

    public void EndAssembly(PageStreamSession session)
    {
        lock (session.Gate)
        {
            session.Assembling = false;
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
        // Hold the session gate for the whole read so a concurrent manifest that changes the
        // identity cannot Reset (delete) the spool out from under the assembler.
        lock (session.Gate)
        {
            AssembleCore(session, sourcePath, pages, destinationPath);
        }
    }

    private void AssembleCore(
        PageStreamSession session,
        string sourcePath,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    )
    {
        var bySource = new Dictionary<string, SpoolPageDescriptor>(StringComparer.Ordinal);
        foreach (SpoolPageDescriptor page in pages)
        {
            bySource.TryAdd(page.SourceName, page);
        }

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (destinationDirectory is not null)
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using ZipArchive source = ZipFile.OpenRead(sourcePath);

        // Guard against a source archive that changed since the descriptors were resolved (e.g. an
        // equal-size/mtime edit): assembling a different image set would silently mix stale spooled
        // bytes with new un-upscaled pages.
        var archiveImages = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in source.Entries)
        {
            if (
                !string.IsNullOrEmpty(entry.Name)
                && ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.FullName))
            )
            {
                archiveImages.Add(entry.FullName);
            }
        }

        if (!archiveImages.SetEquals(bySource.Keys))
        {
            throw new InvalidOperationException(
                $"The source archive for task {session.TaskId} no longer matches the resolved pages; restart the chapter."
            );
        }

        using ZipArchive output = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        // A malformed archive can repeat an entry name; keep only the first output entry per name.
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in source.Entries)
        {
            // Skip directory entries (zip stores them with an empty name).
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (bySource.TryGetValue(entry.FullName, out SpoolPageDescriptor? page))
            {
                string pagePath = session.PagePath(page.Index);
                if (!File.Exists(pagePath))
                {
                    throw new FileNotFoundException(
                        $"Spooled page {page.Index} for task {session.TaskId} is missing.",
                        pagePath
                    );
                }

                if (!written.Add(page.OutputName))
                {
                    throw new InvalidOperationException(
                        $"Two pages of task {session.TaskId} resolve to the same output name '{page.OutputName}'."
                    );
                }

                ZipArchiveEntry outputEntry = output.CreateEntry(page.OutputName);
                using Stream input = File.OpenRead(pagePath);
                using Stream target = outputEntry.Open();
                input.CopyTo(target);
            }
            else
            {
                if (!written.Add(entry.FullName))
                {
                    continue;
                }

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
        // See Assemble: keep a concurrent Reset from deleting the spool mid-read.
        lock (session.Gate)
        {
            AssemblePagesOnlyCore(session, pages, destinationPath);
        }
    }

    private void AssemblePagesOnlyCore(
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

        using ZipArchive output = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (SpoolPageDescriptor page in pages)
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
                        if (GetLatestWriteUtc(directory) < cutoff)
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

    /// <summary>
    /// Newest write time among a spool root and its immediate task directories. Page writes land in
    /// a task subdirectory, which does not update the root's own mtime, so the root alone is a poor
    /// liveness proxy.
    /// </summary>
    private static DateTime GetLatestWriteUtc(string directory)
    {
        DateTime latest = Directory.GetLastWriteTimeUtc(directory);
        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            DateTime subTime = Directory.GetLastWriteTimeUtc(sub);
            if (subTime > latest)
            {
                latest = subTime;
            }
        }

        return latest;
    }
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
    public Lock Gate { get; } = new();
    public DateTime LastTouchedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Bytes committed to this session, for the per-task spool budget.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Committed byte count per page index, so a re-upload replaces rather than adds.</summary>
    public Dictionary<int, long> PageSizes { get; } = new();

    /// <summary>Set while a caller is finalizing (assembling) this session.</summary>
    public bool Assembling { get; set; }

    public string PagePath(int pageIndex) => Path.Combine(Directory, $"page_{pageIndex:D5}.bin");

    public void Reset(string identity, int pageCount, string directory)
    {
        DeleteDirectory();
        Identity = identity;
        PageCount = pageCount;
        Directory = directory;
        Completed.Clear();
        PageSizes.Clear();
        TotalBytes = 0;
        // Clear a stale flag from an assembly that was interrupted before EndAssembly, or the new
        // identity could never finalize.
        Assembling = false;
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
