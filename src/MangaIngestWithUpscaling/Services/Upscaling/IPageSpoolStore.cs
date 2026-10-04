namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Stores the upscaled pages of a page-streamed task and assembles the final chapter from them.
/// The handlers depend on this seam rather than on the concrete spool, so the process-local
/// constraint is stated once here instead of being repeated at every call site.
///
/// The production implementation, <see cref="PageStreamSpool"/>, keeps a task's session in memory
/// and its page files on local disk, so it is <b>process-local by design</b>: the manifest that
/// creates a task's session, the page fetches and the page uploads must all reach the same replica.
/// This interface exposes the mutable concrete <see cref="PageStreamSession"/> and the spool's
/// budget internals and omits lifecycle operations such as <c>Remove</c>/<c>SweepStale</c>, so a
/// distributed adapter is not achievable without reworking the handlers; a deployment must instead
/// pin every RPC for a chapter to one replica. This mirrors the single-replica constraint recorded
/// in <c>docs/PAGE_STREAMING_KNOWN_LIMITATIONS.md</c>.
/// </summary>
public interface IPageSpoolStore
{
    /// <summary>
    /// Returns the session for a task, creating it or resetting it when the content identity or the
    /// engine identity changed. A reset discards the spool so pages produced by different engines are
    /// never mixed into one chapter.
    /// </summary>
    PageStreamSession GetOrCreateSession(
        int taskId,
        string identity,
        string engineIdentity,
        int pageCount
    );

    /// <summary>
    /// Returns the session for a task if the manifest for this replica created one. An upload that
    /// finds no session is on the wrong replica (or skipped the manifest); that must be surfaced
    /// rather than silently spooled into a session that will never finalize.
    /// </summary>
    PageStreamSession? TryGetSession(int taskId);

    /// <summary>The indexes of the pages committed to the session so far.</summary>
    IReadOnlyCollection<int> GetCompletedPages(PageStreamSession session);

    /// <summary>
    /// Opens a unique temp file for a page so a caller can stream bytes straight to disk instead of
    /// buffering the whole page in memory. The page is only recorded as done once
    /// <see cref="TryCommitPage"/> moves the temp file into place.
    /// </summary>
    FileStream BeginPageWrite(PageStreamSession session, int pageIndex, out string tempPath);

    /// <summary>
    /// Atomically moves a completed page into place and records it as done, but only while the
    /// session still holds <paramref name="expectedIdentity"/> and
    /// <paramref name="expectedEngineIdentity"/>. A session reset by a concurrent manifest (content
    /// or engine changed) is stale, so the page is dropped instead of being mixed into the new
    /// chapter. The per-task byte budget is enforced here, under the session gate.
    /// </summary>
    CommitPageResult TryCommitPage(
        PageStreamSession session,
        string expectedIdentity,
        string expectedEngineIdentity,
        int pageIndex,
        string tempPath,
        long size = 0
    );

    /// <summary>
    /// Reserves in-flight bytes for a page that is still streaming, so many concurrent uploads
    /// cannot each write up to <c>MaxPageBytes</c> to temp before any committed-byte check runs.
    /// Returns false when the reservation would exceed the per-task budget. The returned
    /// <paramref name="generation"/> must be passed to <see cref="ReleaseInFlight"/>.
    /// </summary>
    bool TryReserveInFlight(
        PageStreamSession session,
        int pageIndex,
        long bytes,
        out long generation
    );

    /// <summary>Releases a reservation made by <see cref="TryReserveInFlight"/>.</summary>
    void ReleaseInFlight(PageStreamSession session, long generation, long bytes);

    /// <summary>True once every page of the session has been committed.</summary>
    bool IsComplete(PageStreamSession session);

    /// <summary>
    /// Claims the one-shot finalize for a session, so two workers that both see the chapter as
    /// complete cannot assemble it concurrently. Also refuses when the session's identity no longer
    /// matches the caller's, so a stale worker cannot finalize the new identity's chapter.
    /// </summary>
    bool TryBeginAssembly(PageStreamSession session, string expectedIdentity);

    /// <summary>
    /// True once the session has been finalized (removed or swept). A caller that held the session
    /// across a concurrent finalize must restart rather than report the discarded chapter as done.
    /// </summary>
    bool IsFinalized(PageStreamSession session);

    /// <summary>Releases the one-shot finalize claimed by <see cref="TryBeginAssembly"/>.</summary>
    void EndAssembly(PageStreamSession session);

    /// <summary>
    /// Removes pages whose spooled file is missing from the completed set, so a restarted chapter
    /// re-fetches them instead of failing assembly again. Recomputes the committed byte total.
    /// </summary>
    void ForgetMissingPages(PageStreamSession session);

    /// <summary>
    /// Builds the final CBZ at <paramref name="destinationPath"/> from the source archive: every
    /// non-image entry is copied unchanged and every image entry is replaced by its spooled page,
    /// written under the page's output name. Source order is preserved.
    /// </summary>
    void Assemble(
        PageStreamSession session,
        string expectedIdentity,
        string sourcePath,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    );

    /// <summary>
    /// Builds a CBZ containing only the spooled pages, named by their output names. Used for
    /// repair, where the result is merged into the existing upscaled chapter rather than replacing
    /// it.
    /// </summary>
    void AssemblePagesOnly(
        PageStreamSession session,
        string expectedIdentity,
        IReadOnlyList<SpoolPageDescriptor> pages,
        string destinationPath
    );

    /// <summary>
    /// Detaches one specific session (compare-and-remove), so a concurrent manifest that replaced the
    /// task's session with a fresh one is not detached by mistake. Returns the directory to delete,
    /// or null when this instance is no longer the task's current session.
    /// </summary>
    string? Detach(PageStreamSession session);

    /// <summary>Deletes a detached session's directory. Best-effort.</summary>
    void DeleteDirectory(string directory);
}
