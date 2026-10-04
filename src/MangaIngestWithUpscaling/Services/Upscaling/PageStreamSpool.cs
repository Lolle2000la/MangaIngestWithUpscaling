using System.Collections.Concurrent;
using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>A page of a streamed chapter, in source-archive order.</summary>
public sealed record SpoolPageDescriptor(int Index, string SourceName, string OutputName);

/// <summary>Outcome of committing a spooled page.</summary>
public enum CommitPageResult
{
    /// <summary>The page was moved into place and recorded.</summary>
    Committed,

    /// <summary>
    /// The page cannot be committed into this session: its content identity no longer matches the
    /// caller's, or the spool was reset and discarded the page's temp file. The page was dropped.
    /// </summary>
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

    /// <summary>
    /// Rejects archive entry names that could escape the output archive on extraction (absolute or
    /// parent-traversing paths). Shared with the manifest builder so the descriptor set and the
    /// assembled archive agree.
    /// </summary>
    public static bool IsSafeEntryName(string name) =>
        !string.IsNullOrEmpty(name)
        && !Path.IsPathRooted(name)
        // Reject Windows drive/UNC forms independent of the host OS: on Linux Path.IsPathRooted
        // returns false for "C:\evil.jpg" and "\\server\share\evil.jpg". Only a drive prefix is
        // rejected, not a colon anywhere: a colon is legal on Linux and rejecting it silently dropped
        // legitimate pages (the chapter was still reported upscaled).
        && !name.StartsWith('/')
        && !name.StartsWith('\\')
        && !IsWindowsDriveForm(name)
        && !name.Split('/', '\\').Any(IsUnsafeSegment);

    /// <summary>
    ///     True for a Windows absolute drive prefix ("C:\", "C:/"), which must not be interpreted as a
    ///     drive path even when the host is Linux. A drive-relative "C:evil" and a legal Linux name like
    ///     "a:b.jpg" are not rejected: dropping them silently lost pages.
    /// </summary>
    private static bool IsWindowsDriveForm(string name) =>
        name.Length >= 3
        && char.IsAsciiLetter(name[0])
        && name[1] == ':'
        && (name[2] == '/' || name[2] == '\\');

    /// <summary>
    /// True for a path component that is a parent traversal. Windows removes trailing spaces and dots
    /// from a component, so ".. " normalizes to ".." there. A "." component and an empty component
    /// (from "a//b") collapse harmlessly and must be preserved: rejecting them silently dropped pages
    /// while the chapter was still reported upscaled.
    /// </summary>
    private static bool IsUnsafeSegment(string segment)
    {
        // Anything with a non-dot/space character is an ordinary name.
        if (segment.TrimEnd(' ', '.').Length > 0)
        {
            return false;
        }

        // Only dots and spaces: a parent traversal when it has at least two dots ("..", ".. ", "...").
        return segment.Count(c => c == '.') >= 2;
    }

    private readonly ConcurrentDictionary<int, PageStreamSession> _sessions = new();
    private readonly ILogger<PageStreamSpool> _logger;
    private readonly long _maxTaskBytes;

    public PageStreamSpool(ILogger<PageStreamSpool> logger, IOptions<UpscalerConfig> config)
    {
        _logger = logger;
        _maxTaskBytes =
            config.Value.MaxSpoolBytesPerTask > 0
                ? config.Value.MaxSpoolBytesPerTask
                : MaxTaskBytes;

        // Unique per PageStreamSpool instance (and therefore per process): two app instances, or two
        // test collections, on one host must not share task directories.
        string root = string.IsNullOrWhiteSpace(config.Value.SpoolDirectory)
            ? Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling", "page_spool")
            : Path.GetFullPath(config.Value.SpoolDirectory);
        SpoolRoot = Path.Combine(root, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
    }

    public string SpoolRoot { get; }

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
        while (true)
        {
            PageStreamSession session = _sessions.GetOrAdd(
                taskId,
                _ => NewSession(taskId, identity, engineIdentity, pageCount)
            );

            string? discardedDirectory = null;
            lock (session.Gate)
            {
                if (session.Finalized)
                {
                    // A concurrent Remove/SweepStale finalized this instance after GetOrAdd read it:
                    // its directory is gone, so replace it with a fresh session rather than hand back
                    // a dead one (BeginPageWrite would otherwise recreate the deleted directory).
                    var replacement = NewSession(taskId, identity, engineIdentity, pageCount);
                    if (_sessions.TryUpdate(taskId, replacement, session))
                    {
                        return replacement;
                    }

                    continue;
                }

                if (session.Identity != identity || session.EngineIdentity != engineIdentity)
                {
                    if (session.Assembling)
                    {
                        // A finalize/assembly is in progress. Resetting now would delete the spool out
                        // from under the finalizer, which would then commit a superseded identity's
                        // results. Leave the session alone: the caller sees the old identity and
                        // restarts its worker, and once assembly finishes (and removes the session) a
                        // later manifest creates a fresh one.
                        session.LastTouchedUtc = DateTime.UtcNow;
                        return session;
                    }

                    _logger.LogInformation(
                        "Page spool identity changed for task {TaskId} (content or engine); discarding {Count} spooled page(s).",
                        taskId,
                        session.Completed.Count
                    );
                    // A fresh directory per reset: the discarded session's directory is unique, so a
                    // stale finalizer deleting it can never wipe the new session's files.
                    discardedDirectory = session.Reset(
                        identity,
                        engineIdentity,
                        pageCount,
                        NewSessionDirectory(taskId)
                    );
                }
                else
                {
                    session.PageCount = pageCount;
                }

                session.LastTouchedUtc = DateTime.UtcNow;
                TouchRoot();
            }

            if (discardedDirectory is not null)
            {
                // Delete off the session gate: the recursive delete can be many gigabytes, and holding
                // the gate across it would wedge every page RPC for this task behind it. The directory
                // is unique and unreferenced, so deleting it later is safe.
                string directory = discardedDirectory;
                _ = Task.Run(() => DeleteDirectory(directory));
            }

            return session;
        }
    }

    private PageStreamSession NewSession(
        int taskId,
        string identity,
        string engineIdentity,
        int pageCount
    ) => new(taskId, identity, engineIdentity, pageCount, NewSessionDirectory(taskId));

    /// <summary>
    /// A directory unique to one session instance, so deleting a removed or swept session can never
    /// wipe a freshly created session for the same task id.
    /// </summary>
    private string NewSessionDirectory(int taskId) =>
        Path.Combine(SpoolRoot, $"{taskId}_{Guid.NewGuid():N}");

    /// <summary>
    ///     Refreshes the spool root's timestamp, which is what another instance's sweep judges liveness
    ///     by. Session activity is tracked in memory, so without this a replica sharing the temp
    ///     directory could take a chapter that is still streaming for idle and reclaim its pages.
    /// </summary>
    private void TouchRoot()
    {
        try
        {
            File.SetLastWriteTimeUtc(SpoolRoot, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh the page spool root timestamp.");
        }
    }

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
        try
        {
            await using (output)
            {
                await content.CopyToAsync(output, cancellationToken);
            }

            CommitPage(session, pageIndex, temp);
        }
        catch
        {
            // A cancelled or failed copy leaves the temp file behind, and the page was never committed,
            // so nothing else can reach it.
            DeleteTempFile(temp);
            throw;
        }
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
            if (session.Finalized)
            {
                // The session was detached/swept after the caller resolved it; refuse rather than
                // recreate the deleted directory (which would leak an empty orphan dir that the
                // parent-only sweep never reclaims).
                throw new PageStreamRestartException(
                    $"The page spool for task {session.TaskId} was finalized; restart the chapter.",
                    resetSpool: true
                );
            }

            // Build the path and open the temp file under the gate: a concurrent Reset/Detach can
            // delete the session directory, which would otherwise turn a clean identity rejection into
            // an unhandled DirectoryNotFoundException.
            Directory.CreateDirectory(session.Directory);
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
            // A removed/finalized session is terminal; committing into it would recreate the
            // directory that Remove just deleted.
            if (session.Finalized)
            {
                return CommitPageResult.IdentityMismatch;
            }

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

            // The temp file sits in the directory this session had when the page was opened, and any
            // reset deletes that directory — including a reset that lands back on this same identity,
            // which passes the checks above with a temp file that no longer exists. Drop such a stale
            // page here instead of letting File.Move throw out of the commit.
            if (!File.Exists(tempPath))
            {
                return CommitPageResult.IdentityMismatch;
            }

            // Re-uploading a page replaces its bytes; adjust by the delta so legitimate retries do
            // not spuriously trip the per-task budget.
            long previous = session.PageSizes.TryGetValue(pageIndex, out long prev) ? prev : 0;
            if (session.TotalBytes - previous + size > _maxTaskBytes)
            {
                return CommitPageResult.OverBudget;
            }

            try
            {
                File.Move(tempPath, session.PagePath(pageIndex), overwrite: true);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            {
                // A concurrent identity reset deleted this session's directory (and the temp file in
                // it) between the existence check above and the move; the page is stale, drop it.
                return CommitPageResult.IdentityMismatch;
            }

            session.Completed.Add(pageIndex);
            session.PageSizes[pageIndex] = size;
            session.TotalBytes += size - previous;
            session.LastTouchedUtc = DateTime.UtcNow;
            TouchRoot();
            return CommitPageResult.Committed;
        }
    }

    /// <summary>Atomically moves a completed page into place and records it as done.</summary>
    public void CommitPage(PageStreamSession session, int pageIndex, string tempPath)
    {
        // Read the size defensively: a reset may delete the temp file between the caller creating it
        // and this call, and reading FileInfo.Length would otherwise throw FileNotFoundException out
        // of the public API instead of yielding TryCommitPage's clean IdentityMismatch drop.
        long size;
        try
        {
            size = new FileInfo(tempPath).Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            DeleteTempFile(tempPath);
            return;
        }

        CommitPageResult result = TryCommitPage(
            session,
            session.Identity,
            session.EngineIdentity,
            pageIndex,
            tempPath,
            size
        );
        if (result != CommitPageResult.Committed)
        {
            DeleteTempFile(tempPath);

            throw new InvalidOperationException(
                $"Failed to commit page {pageIndex} for task {session.TaskId}: {result}."
            );
        }
    }

    /// <summary>Best-effort removal of a page temp file that was never committed.</summary>
    private void DeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            // The session directory may already have been reclaimed, taking the temp file with it.
            _logger.LogDebug(ex, "Failed to delete uncommitted page temp file {Temp}.", tempPath);
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
    /// Removes pages whose spooled file is missing from the completed set, so a restarted chapter
    /// re-fetches them instead of failing assembly again. Recomputes the committed byte total.
    /// </summary>
    public void ForgetMissingPages(PageStreamSession session)
    {
        lock (session.Gate)
        {
            long total = 0;
            foreach (int index in session.Completed.ToArray())
            {
                if (File.Exists(session.PagePath(index)))
                {
                    total += session.PageSizes.TryGetValue(index, out long size) ? size : 0;
                    continue;
                }

                session.Completed.Remove(index);
                session.PageSizes.Remove(index);
            }

            session.TotalBytes = total;
        }
    }

    /// <summary>
    /// Reserves in-flight bytes for a page that is still streaming, so many concurrent uploads
    /// cannot each write up to <c>MaxPageBytes</c> to temp before any committed-byte check runs.
    /// Returns false when the reservation would exceed <see cref="MaxTaskBytes"/>. A re-upload of a
    /// page that is already committed is measured against the bytes it replaces. The returned
    /// <paramref name="generation"/> must be passed to <see cref="ReleaseInFlight"/> so a reservation
    /// made under an identity that was since reset cannot corrupt the new identity's accounting.
    /// </summary>
    public bool TryReserveInFlight(
        PageStreamSession session,
        int pageIndex,
        long bytes,
        out long generation
    )
    {
        lock (session.Gate)
        {
            generation = session.Generation;
            // A re-upload replaces the page's committed bytes, so account for the replaced page
            // rather than double-counting it against the budget.
            long previous = session.PageSizes.TryGetValue(pageIndex, out long prev) ? prev : 0;
            if (
                session.TotalBytes - previous + session.InFlightByGeneration.Values.Sum() + bytes
                > _maxTaskBytes
            )
            {
                return false;
            }

            session.InFlightByGeneration[generation] =
                session.InFlightByGeneration.GetValueOrDefault(generation) + bytes;
            return true;
        }
    }

    /// <summary>Releases a reservation made by <see cref="TryReserveInFlight"/>.</summary>
    public void ReleaseInFlight(PageStreamSession session, long generation, long bytes)
    {
        lock (session.Gate)
        {
            // A reset (identity change) already discarded the reservation; releasing it here would
            // subtract from the new identity's in-flight accounting.
            if (!session.InFlightByGeneration.TryGetValue(generation, out long reserved))
            {
                return;
            }

            long remaining = reserved - bytes;
            if (remaining > 0)
            {
                session.InFlightByGeneration[generation] = remaining;
            }
            else
            {
                session.InFlightByGeneration.Remove(generation);
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
            return TryBeginAssemblyLocked(session, expectedIdentity);
        }
    }

    public bool TryBeginAssembly(PageStreamSession session)
    {
        // Read the identity under the gate: reading it outside could race a Reset that changes it
        // between the read and the check.
        lock (session.Gate)
        {
            return TryBeginAssemblyLocked(session, session.Identity);
        }
    }

    private static bool TryBeginAssemblyLocked(PageStreamSession session, string expectedIdentity)
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

    /// <summary>
    /// True once the session has been finalized (removed or swept). A caller that held the session
    /// across a concurrent finalize must restart rather than report the discarded chapter as done.
    /// </summary>
    public bool IsFinalized(PageStreamSession session)
    {
        lock (session.Gate)
        {
            return session.Finalized;
        }
    }

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
        string expectedIdentity,
        string sourcePath,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    )
    {
        // Hold the session gate for the whole read so a concurrent manifest that changes the
        // identity cannot Reset (delete) the spool out from under the assembler, and re-validate the
        // identity under the gate: TryBeginAssembly ran earlier and released the gate, so a Reset in
        // between could otherwise mix two identities' page bytes into one archive.
        lock (session.Gate)
        {
            EnsureAssemblyIdentity(session, expectedIdentity);
            AssembleCore(session, sourcePath, pages, destinationPath);
        }
    }

    /// <summary>
    /// Throws a restart if the session was finalized or its identity changed since the caller won
    /// <see cref="TryBeginAssembly"/>. Must be called under the session gate.
    /// </summary>
    private static void EnsureAssemblyIdentity(PageStreamSession session, string expectedIdentity)
    {
        if (
            session.Finalized
            || !string.Equals(session.Identity, expectedIdentity, StringComparison.Ordinal)
        )
        {
            throw new PageStreamRestartException(
                $"The spool for task {session.TaskId} was reset while assembling; restart the chapter.",
                resetSpool: true
            );
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
                && IsSafeEntryName(entry.FullName)
            )
            {
                archiveImages.Add(entry.FullName);
            }
        }

        if (!archiveImages.SetEquals(bySource.Keys))
        {
            throw new PageStreamRestartException(
                $"The source archive for task {session.TaskId} no longer matches the resolved pages; restart the chapter.",
                resetSpool: true
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

            // Do not propagate a parent-traversing name into the output archive.
            if (!IsSafeEntryName(entry.FullName))
            {
                continue;
            }

            if (bySource.TryGetValue(entry.FullName, out SpoolPageDescriptor? page))
            {
                string pagePath = session.PagePath(page.Index);
                if (!File.Exists(pagePath))
                {
                    throw new PageStreamRestartException(
                        $"Spooled page {page.Index} for task {session.TaskId} is missing."
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
        string expectedIdentity,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    )
    {
        // See Assemble: keep a concurrent Reset from deleting the spool mid-read, and re-validate the
        // identity under the gate.
        lock (session.Gate)
        {
            EnsureAssemblyIdentity(session, expectedIdentity);
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

        // ZipArchiveMode.Create uses FileMode.CreateNew; a re-finalize into the same prepared repair
        // target would otherwise throw an opaque IOException.
        File.Delete(destinationPath);

        using ZipArchive output = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (SpoolPageDescriptor page in pages)
        {
            string pagePath = session.PagePath(page.Index);
            if (!File.Exists(pagePath))
            {
                throw new PageStreamRestartException(
                    $"Spooled page {page.Index} for task {session.TaskId} is missing."
                );
            }

            ZipArchiveEntry outputEntry = output.CreateEntry(page.OutputName);
            using Stream input = File.OpenRead(pagePath);
            using Stream target = outputEntry.Open();
            input.CopyTo(target);
        }
    }

    /// <summary>
    /// Removes a session from the spool and finalizes it (cheap, in-memory), returning its directory
    /// for the caller to delete. The recursive delete can be many gigabytes, so a caller on a hot path
    /// should schedule <see cref="DeleteDirectory"/> rather than block on it.
    /// </summary>
    public string? Detach(int taskId)
    {
        if (_sessions.TryRemove(taskId, out PageStreamSession? session))
        {
            lock (session.Gate)
            {
                // Terminal under the gate: a concurrent TryBeginAssembly must not re-run assembly on
                // a session whose directory is being deleted.
                session.Finalized = true;
                return session.Directory;
            }
        }

        return null;
    }

    /// <summary>
    /// Detaches one specific session (compare-and-remove), so a concurrent manifest that replaced the
    /// task's session with a fresh one is not detached by mistake. Returns the directory to delete,
    /// or null when this instance is no longer the task's current session.
    /// </summary>
    public string? Detach(PageStreamSession session)
    {
        if (_sessions.TryRemove(new KeyValuePair<int, PageStreamSession>(session.TaskId, session)))
        {
            lock (session.Gate)
            {
                session.Finalized = true;
                return session.Directory;
            }
        }

        return null;
    }

    /// <summary>Deletes a detached session's directory. Best-effort.</summary>
    public void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete page spool directory {Directory}.", directory);
        }
    }

    public void Remove(int taskId)
    {
        string? directory = Detach(taskId);
        if (directory is not null)
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Deletes sessions idle longer than <paramref name="retention"/>.</summary>
    public void SweepStale(TimeSpan retention)
    {
        DateTime cutoff = DateTime.UtcNow - retention;
        foreach ((int taskId, PageStreamSession session) in _sessions)
        {
            // Check and remove atomically under the gate: a manifest/upload that refreshed
            // LastTouchedUtc, or a finalize/reset that replaced the session, must not be swept. The
            // compare-and-remove overload removes only this exact instance, so a newer live session is
            // left alone.
            lock (session.Gate)
            {
                if (
                    session.LastTouchedUtc >= cutoff
                    || session.Assembling
                    || !_sessions.TryRemove(
                        new KeyValuePair<int, PageStreamSession>(taskId, session)
                    )
                )
                {
                    continue;
                }

                _logger.LogInformation("Removing stale page spool for task {TaskId}.", taskId);
                session.Finalized = true;
                session.DeleteDirectory(_logger);
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

    /// <summary>
    /// Bytes reserved by uploads that are still streaming, counted against the budget. Tracked per
    /// generation so a reservation made under a discarded identity cannot be released against (or
    /// leak into) the new identity's accounting.
    /// </summary>
    public Dictionary<long, long> InFlightByGeneration { get; } = new();

    /// <summary>
    /// Bumped on every reset. A reservation or release tagged with a stale generation is ignored, so
    /// an in-flight upload under a discarded identity cannot corrupt the new identity's accounting.
    /// </summary>
    public long Generation { get; private set; }

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

    /// <summary>
    /// Discards this session's spool for a new identity, returning the discarded directory so the
    /// caller can delete it off the session gate (the recursive delete can be many gigabytes).
    /// </summary>
    public string? Reset(string identity, string engineIdentity, int pageCount, string directory)
    {
        string? discardedDirectory = Directory;
        Identity = identity;
        EngineIdentity = engineIdentity;
        PageCount = pageCount;
        Directory = directory;
        Completed.Clear();
        PageSizes.Clear();
        TotalBytes = 0;
        InFlightByGeneration.Clear();
        Generation++;
        // Clear a stale flag from an assembly that was interrupted before EndAssembly, or the new
        // identity could never finalize.
        Assembling = false;
        Finalized = false;
        return discardedDirectory;
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
