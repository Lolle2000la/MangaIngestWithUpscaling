using System.Collections.Concurrent;
using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Constants;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>A page of a streamed chapter, in source-archive order.</summary>
public sealed record SpoolPageDescriptor(int Index, string SourceName, string OutputName);

/// <summary>Outcome of committing a spooled page.</summary>
public enum CommitPageResult
{
    /// <summary>The page was moved into place and recorded.</summary>
    Committed,

    /// <summary>The session's content identity no longer matches the caller's; the page was dropped.</summary>
    IdentityMismatch,

    /// <summary>The session's engine identity differs from the caller's; the page was dropped.</summary>
    EngineMismatch,

    /// <summary>Committing the page would exceed <see cref="PageStreamSpool.MaxTaskBytes"/>.</summary>
    OverBudget,
}

/// <summary>
/// Process-local spool of upscaled pages for page-streamed tasks. Pages are written atomically as
/// they arrive and the final CBZ is assembled from the source archive plus the spooled pages, so a
/// dropped connection only loses the in-flight pages and a re-dispatched task resumes at the first
/// missing page.
///
/// The spool is keyed by a content identity; a task re-dispatched with a different identity (source
/// or profile changed) has its spool discarded so old and new bytes are never mixed.
///
/// The identity covers the source file and the upscaler profile; the worker additionally supplies an
/// opaque engine identity (its models and preprocessing). A manifest whose content or engine identity
/// differs resets the spool, and a page produced by a different engine is rejected, so pages from
/// different engines are never mixed into one chapter.
/// </summary>
public sealed class PageStreamSpool
{
    /// <summary>Upper bound on the total bytes spooled for one task, so a chapter cannot fill the disk.</summary>
    public const long MaxTaskBytes = 8L * 1024 * 1024 * 1024;

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
    /// Returns the session for a task, creating it or resetting it when the content identity or the
    /// engine identity changed. A reset discards the spool so pages produced by different engines are
    /// never mixed into one chapter.
    /// </summary>
    public PageStreamSession GetOrCreateSession(
        int taskId,
        string identity,
        string engineIdentity,
        int pageCount
    )
    {
        PageStreamSession session = _sessions.GetOrAdd(
            taskId,
            _ => new PageStreamSession(
                taskId,
                identity,
                engineIdentity,
                pageCount,
                NewSessionDirectory(taskId)
            )
        );

        lock (session.Gate)
        {
            if (session.Identity != identity || session.EngineIdentity != engineIdentity)
            {
                _logger.LogInformation(
                    "Page spool identity changed for task {TaskId} (content or engine); discarding {Count} spooled page(s).",
                    taskId,
                    session.Completed.Count
                );
                // A fresh directory per reset: the discarded session's directory is unique, so a
                // stale finalizer deleting it can never wipe the new session's files.
                session.Reset(identity, engineIdentity, pageCount, NewSessionDirectory(taskId));
            }
            else
            {
                session.PageCount = pageCount;
            }

            session.LastTouchedUtc = DateTime.UtcNow;
        }

        return session;
    }

    /// <summary>
    /// A directory unique to one session instance, so deleting a removed or swept session can never
    /// wipe a freshly created session for the same task id.
    /// </summary>
    private string NewSessionDirectory(int taskId) =>
        Path.Combine(SpoolRoot, $"{taskId}_{Guid.NewGuid():N}");

    /// <summary>
    /// Returns the session for a task if the manifest for this replica created one. An upload that
    /// finds no session is on the wrong replica (or skipped the manifest); that must be surfaced
    /// rather than silently spooled into a session that will never finalize.
    /// </summary>
    public PageStreamSession? TryGetSession(int taskId) =>
        _sessions.TryGetValue(taskId, out PageStreamSession? session) ? session : null;

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
    /// session still holds <paramref name="expectedIdentity"/> and
    /// <paramref name="expectedEngineIdentity"/>. A session reset by a concurrent manifest (content
    /// or engine changed) is stale, so the page is dropped instead of being mixed into the new
    /// chapter. The per-task byte budget is checked here, under the gate, so two concurrent uploads
    /// cannot jointly exceed it.
    /// </summary>
    public CommitPageResult TryCommitPage(
        PageStreamSession session,
        string expectedIdentity,
        string expectedEngineIdentity,
        int pageIndex,
        string tempPath,
        long size = 0
    )
    {
        lock (session.Gate)
        {
            if (!string.Equals(session.Identity, expectedIdentity, StringComparison.Ordinal))
            {
                return CommitPageResult.IdentityMismatch;
            }

            if (
                !string.Equals(
                    session.EngineIdentity,
                    expectedEngineIdentity,
                    StringComparison.Ordinal
                )
            )
            {
                return CommitPageResult.EngineMismatch;
            }

            // Re-uploading a page replaces its bytes; adjust by the delta so legitimate retries do
            // not spuriously trip the per-task budget.
            long previous = session.PageSizes.TryGetValue(pageIndex, out long prev) ? prev : 0;
            if (session.TotalBytes - previous + size > MaxTaskBytes)
            {
                return CommitPageResult.OverBudget;
            }

            File.Move(tempPath, session.PagePath(pageIndex), overwrite: true);
            session.Completed.Add(pageIndex);
            session.PageSizes[pageIndex] = size;
            session.TotalBytes += size - previous;
            session.LastTouchedUtc = DateTime.UtcNow;
            return CommitPageResult.Committed;
        }
    }

    /// <summary>Atomically moves a completed page into place and records it as done.</summary>
    public void CommitPage(PageStreamSession session, int pageIndex, string tempPath)
    {
        CommitPageResult result = TryCommitPage(
            session,
            session.Identity,
            session.EngineIdentity,
            pageIndex,
            tempPath,
            new FileInfo(tempPath).Length
        );
        if (result != CommitPageResult.Committed)
        {
            throw new InvalidOperationException(
                $"Failed to commit page {pageIndex} for task {session.TaskId}: {result}."
            );
        }
    }

    /// <summary>Bytes committed to the session so far, for the per-task spool budget.</summary>
    public long GetTotalBytes(PageStreamSession session)
    {
        lock (session.Gate)
        {
            return session.TotalBytes;
        }
    }

    /// <summary>
    /// Reserves in-flight bytes for a page that is still streaming, so many concurrent uploads
    /// cannot each write up to <c>MaxPageBytes</c> to temp before any committed-byte check runs.
    /// Returns false when the reservation would exceed <see cref="MaxTaskBytes"/>.
    /// </summary>
    public bool TryReserveInFlight(PageStreamSession session, long bytes)
    {
        lock (session.Gate)
        {
            if (session.TotalBytes + session.InFlightBytes + bytes > MaxTaskBytes)
            {
                return false;
            }

            session.InFlightBytes += bytes;
            return true;
        }
    }

    /// <summary>Releases a reservation made by <see cref="TryReserveInFlight"/>.</summary>
    public void ReleaseInFlight(PageStreamSession session, long bytes)
    {
        lock (session.Gate)
        {
            session.InFlightBytes -= bytes;
            if (session.InFlightBytes < 0)
            {
                session.InFlightBytes = 0;
            }
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
                session.Finalized
                || session.Assembling
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

        // A malformed archive can repeat an entry name; the first occurrence wins, matching
        // BuildPageDescriptors and GetPages, so a duplicate is skipped here instead of colliding.
        var handledSources = new HashSet<string>(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in source.Entries)
        {
            // Skip directory entries (zip stores them with an empty name).
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!handledSources.Add(entry.FullName))
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

                // BuildPageDescriptors disambiguates distinct sources, so this only fires if that
                // invariant is ever broken; fail loudly rather than silently dropping a page.
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
            lock (session.Gate)
            {
                // Terminal under the gate: a concurrent TryBeginAssembly must not re-run assembly on
                // a session whose directory is being deleted.
                session.Finalized = true;
                session.DeleteDirectory(_logger);
            }
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
                lock (removed.Gate)
                {
                    removed.Finalized = true;
                    removed.DeleteDirectory(_logger);
                }
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
    public PageStreamSession(
        int taskId,
        string identity,
        string engineIdentity,
        int pageCount,
        string directory
    )
    {
        TaskId = taskId;
        Identity = identity;
        EngineIdentity = engineIdentity;
        PageCount = pageCount;
        Directory = directory;
    }

    public int TaskId { get; }
    public string Identity { get; private set; }

    /// <summary>Opaque identity of the engine that produced the spooled pages.</summary>
    public string EngineIdentity { get; private set; }

    public int PageCount { get; set; }
    public string Directory { get; private set; }
    public HashSet<int> Completed { get; } = new();
    public Lock Gate { get; } = new();
    public DateTime LastTouchedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Bytes committed to this session, for the per-task spool budget.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Bytes reserved by uploads that are still streaming, counted against the budget.</summary>
    public long InFlightBytes { get; set; }

    /// <summary>Committed byte count per page index, so a re-upload replaces rather than adds.</summary>
    public Dictionary<int, long> PageSizes { get; } = new();

    /// <summary>Set while a caller is finalizing (assembling) this session.</summary>
    public bool Assembling { get; set; }

    /// <summary>
    /// Set once the session has been removed (finalized). Blocks any further assembly, so a
    /// concurrent upload that observed completion cannot re-run assembly on a deleted spool.
    /// </summary>
    public bool Finalized { get; set; }

    public string PagePath(int pageIndex) => Path.Combine(Directory, $"page_{pageIndex:D5}.bin");

    public void Reset(string identity, string engineIdentity, int pageCount, string directory)
    {
        DeleteDirectory();
        Identity = identity;
        EngineIdentity = engineIdentity;
        PageCount = pageCount;
        Directory = directory;
        Completed.Clear();
        PageSizes.Clear();
        TotalBytes = 0;
        InFlightBytes = 0;
        // Clear a stale flag from an assembly that was interrupted before EndAssembly, or the new
        // identity could never finalize.
        Assembling = false;
        Finalized = false;
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
