using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>How a "finalize a complete spooled chapter" attempt ended.</summary>
public enum PageStreamFinalizeStatus
{
    /// <summary>The session is not complete yet; the caller keeps streaming.</summary>
    NotComplete,

    /// <summary>A concurrent manifest reset the session's identity; the caller must restart.</summary>
    IdentityRaced,

    /// <summary>The session was finalized/removed while the caller held it; the caller must restart.</summary>
    Finalized,

    /// <summary>
    /// Another request already owns the finalize; report the chapter as done without redoing the work.
    /// </summary>
    AlreadyAssembling,

    /// <summary>This request finalized the chapter.</summary>
    FinalizedSuccessfully,

    /// <summary>The finalize threw a recoverable <see cref="PageStreamRestartException"/>; restart.</summary>
    Restart,

    /// <summary>The finalize hit a local storage failure; keep the spool and let the worker retry.</summary>
    TransientFailure,

    /// <summary>The finalize genuinely failed and the task was marked failed.</summary>
    TerminalFailure,
}

/// <summary>
/// Result of <see cref="PageStreamFinalizer.FinalizeAsync"/>. Carries the status plus the data each
/// caller needs to build its own response: the restart/transient/terminal message, whether the spool
/// was reset (so a manifest can re-create a session), and the session to build a manifest from.
/// </summary>
public sealed record PageStreamFinalizeOutcome(
    PageStreamFinalizeStatus Status,
    PageStreamSession Session,
    bool ResetSpool = false,
    string? Message = null
);

/// <summary>
/// Per-call log and task-failure texts. The wording differs by call site (already-complete manifest,
/// upscale assembly, detection finalize) but the log levels and the classification do not, so the
/// texts are passed in rather than hard-coded.
/// </summary>
public sealed record PageStreamFinalizeTexts(
    string RestartLog,
    string TransientLog,
    string TerminalLog,
    string TerminalTaskFailurePrefix
)
{
    public static readonly PageStreamFinalizeTexts ManifestFinalize = new(
        "Restarting an already-complete page stream for task {TaskId}.",
        "Finalizing task {TaskId} hit a storage failure; the worker must retry.",
        "Failed to finalize an already-complete page stream for task {TaskId}.",
        "Finalizing the chapter failed: "
    );

    public static readonly PageStreamFinalizeTexts UpscaleAssembly = new(
        "Assembly of task {TaskId} must restart.",
        "Assembly of task {TaskId} hit a storage failure; keeping the spool for a retry.",
        "Failed to assemble upscaled chapter for task {TaskId}",
        "Assembling the chapter failed: "
    );

    public static readonly PageStreamFinalizeTexts DetectionFinalize = new(
        "Detection finalize for task {TaskId} must restart.",
        "Finalizing detection for task {TaskId} hit a storage failure; keeping the spool for a retry.",
        "Failed to process page-streamed detection results for task {TaskId}",
        "Finalizing detection failed: "
    );
}

/// <summary>
/// Owns the one-shot "finalize a complete spooled chapter" ladder shared by the manifest and the two
/// upload handlers: the complete check, the identity re-check, the assembly claim, the finalize call,
/// the restart/transient/terminal classification, and releasing the claim in a finally.
///
/// The finalize action itself (upscale assembly or detection finalize), dropping the spool and marking
/// a task failed stay with the caller, which passes them in as delegates; this keeps the type free of
/// the handlers' request/response types and lets each caller map the outcome to its own response.
/// </summary>
public sealed class PageStreamFinalizer(IPageSpoolStore spool, ILogger logger)
{
    /// <summary>
    /// Runs the ladder for one session. A stale identity, a finalized session and a restart are
    /// non-terminal; a genuine failure is terminal (and marks the task failed); another worker owning
    /// the finalize is reported as success.
    /// </summary>
    public async Task<PageStreamFinalizeOutcome> FinalizeAsync(
        PageStreamSession session,
        string identity,
        Func<Task> finalizeAsync,
        Action<PageStreamSession> dropSpool,
        Func<int, string, Task> markTaskFailedAsync,
        PageStreamFinalizeTexts texts
    )
    {
        if (!spool.IsComplete(session))
        {
            // A concurrent manifest can reset the session between the page commit and here, so the
            // page was just discarded and IsComplete is false for the new identity. Reporting
            // NotComplete would be mapped to success by the upload handlers for a page that no longer
            // exists, and a last-page upload could strand the task. Re-check identity/finalize first.
            if (!string.Equals(session.Identity, identity, StringComparison.Ordinal))
            {
                return new PageStreamFinalizeOutcome(
                    PageStreamFinalizeStatus.IdentityRaced,
                    session
                );
            }

            if (spool.IsFinalized(session))
            {
                return new PageStreamFinalizeOutcome(PageStreamFinalizeStatus.Finalized, session);
            }

            return new PageStreamFinalizeOutcome(PageStreamFinalizeStatus.NotComplete, session);
        }

        if (!spool.TryBeginAssembly(session, identity))
        {
            // False also covers a concurrent identity reset, which the worker must retry rather than
            // treat as "someone else is finalizing".
            if (!string.Equals(session.Identity, identity, StringComparison.Ordinal))
            {
                return new PageStreamFinalizeOutcome(
                    PageStreamFinalizeStatus.IdentityRaced,
                    session
                );
            }

            if (spool.IsFinalized(session))
            {
                return new PageStreamFinalizeOutcome(PageStreamFinalizeStatus.Finalized, session);
            }

            return new PageStreamFinalizeOutcome(
                PageStreamFinalizeStatus.AlreadyAssembling,
                session
            );
        }

        // The winning session: a reset-spool restart below drops it, and EndAssembly must clear the
        // flag on the session that actually won the claim.
        PageStreamSession assemblySession = session;
        try
        {
            await finalizeAsync();
            // Drop the session while it is still marked assembling, so a concurrent manifest cannot
            // replace it between EndAssembly and the detach and have its fresh session detached.
            dropSpool(session);
        }
        catch (PageStreamRestartException ex)
        {
            // Recoverable: tell the worker to stream the chapter again instead of failing it.
            logger.LogWarning(ex, texts.RestartLog, session.TaskId);
            if (ex.ResetSpool)
            {
                dropSpool(session);
            }
            else
            {
                spool.ForgetMissingPages(session);
            }

            return new PageStreamFinalizeOutcome(
                PageStreamFinalizeStatus.Restart,
                session,
                ex.ResetSpool,
                ex.Message
            );
        }
        catch (Exception ex)
        {
            if (IsTransientStorageFailure(ex))
            {
                logger.LogWarning(ex, texts.TransientLog, session.TaskId);
                return new PageStreamFinalizeOutcome(
                    PageStreamFinalizeStatus.TransientFailure,
                    session,
                    false,
                    ex.Message
                );
            }

            // Do not report success: the worker would return cleanly and the task would linger in
            // Processing. Mark it failed so it is retried or surfaced, and tell the worker.
            logger.LogError(ex, texts.TerminalLog, session.TaskId);
            await markTaskFailedAsync(session.TaskId, texts.TerminalTaskFailurePrefix + ex.Message);
            return new PageStreamFinalizeOutcome(
                PageStreamFinalizeStatus.TerminalFailure,
                session,
                false,
                ex.Message
            );
        }
        finally
        {
            spool.EndAssembly(assemblySession);
        }

        return new PageStreamFinalizeOutcome(
            PageStreamFinalizeStatus.FinalizedSuccessfully,
            session
        );
    }

    /// <summary>
    ///     True when <paramref name="ex" /> — or something it wraps — is a local storage failure: a full
    ///     disk, a flaky mount, a read-only volume. That is the class of error the spool exists to
    ///     absorb, so every page-write, assembly and finalize path treats it the same way: keep the
    ///     spool and let the worker restart the chapter, instead of deleting a chapter's worth of
    ///     already-upscaled pages.
    /// </summary>
    internal static bool IsTransientStorageFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
