# Page streaming — known limitations and investigation leads

This page records behaviour that is known, deliberately scoped, or only safe under an assumption.
None of it is known to corrupt data today, but each item lists the symptom to watch for, where the
relevant code lives, and the direction a fix should take, so a future failure can be traced back to
its cause quickly.

The page-streaming design itself is described in [REMOTE_WORKER.md](REMOTE_WORKER.md).

## Resume is per server process, not across a server restart

- **Where:** `PageStreamSpool` (`SpoolRoot`, the in-memory `_sessions`/completed set) and the
  page-streaming contract in `protos/upscaling-dist.proto` ("a re-dispatched task resumes where it
  left off").
- **Current behaviour:** within **one server process lifetime** a dropped worker or connection
  resumes where it left off: the manifest reports the already-spooled pages and the worker only
  re-produces the missing ones. Across a **server restart** there is no resume: the completed set
  is in memory and each process owns a unique spool root (`{SpoolParent}/{ProcessId}-{Guid}`), so
  the restarted process starts with an empty session and re-streams (and re-upscales) the whole
  chapter. The old page bytes may survive on disk — a graceful shutdown deletes the process's own
  root; a crash leaves it until the startup/periodic sweep ages it out (24 h retention) — but the
  new session directory cannot see them, so they are orphaned.
- **Symptom to watch for:** after a server restart, in-flight chapters upscale from scratch again
  and temp disk usage briefly holds two copies of the chapter until the old spool root is swept.
- **Direction (follow-up, deliberately not implemented here):** persist the per-task session
  (identity + completed-page set) so a restarted process can adopt an existing spool root and
  resume rather than re-stream. That requires discovering and identity-validating the on-disk page
  files before reuse, which is a separate, riskier change.

## Spool location and budget (Finding 3)

- **Where:** `PageStreamSpool` (`SpoolRoot`, `_maxTaskBytes`).
- **Current behaviour:** the root and the per-task budget are configurable
  (`UpscalerConfig.SpoolDirectory`, `UpscalerConfig.MaxSpoolBytesPerTask`, default 8 GiB); the root
  defaults to a subdirectory of the system temp directory. There is still **no process-wide cap**, so
  several large chapters spooling at once are bounded only by the sum of their per-task budgets.
- **Symptom to watch for:** a container whose `/tmp` is a tmpfs (RAM-backed) exhausting host memory
  when several large chapters spool at once even though each is within its per-task budget.
- **Direction:** set `SpoolDirectory` to a real disk volume and consider a global (process-wide)
  budget in addition to the per-task one.

## Worker upload backlog (Finding 5) — fixed

`PageStreamClient` now bounds the in-flight upload backlog with a semaphore (`UploadBacklogLimit`)
acquired in the page-done callback and released after each upload, so a fast GPU on a slow link
pauses the worker instead of accumulating a whole extra copy of the chapter on disk. Temp usage is
O(backlog), not O(chapter).

## Manifest deadline vs. inline assembly (Finding 10)

- **Where:** `PageStreamClient.ManifestDeadline` / `UpscalerConfig.ManifestTimeout` and the
  manifest-complete branch in `UpscalingDistributionService.Pages` (which assembles the CBZ inline
  before replying).
- **Current behaviour:** the deadline is configurable (`ManifestTimeout`, default 10 minutes). The
  manifest normally returns immediately, but when a chapter is already fully spooled the server
  assembles the final CBZ inside the call; a chapter large enough to exceed the configured deadline
  on slow storage still gets a transient `DeadlineExceeded`, so the worker requeues and the assembly
  is retried (and can eventually fail the chapter).
- **Symptom to watch for:** a large already-spooled chapter repeatedly re-streaming or failing with
  deadline errors, with assembly log lines spanning more than `ManifestTimeout`.
- **Direction:** separate "the chapter is complete" from "the CBZ has been assembled": answer the
  manifest immediately and finalize asynchronously (the worker returns and the server completes the
  task), so the deadline stops covering the assembly at all.

## Preprocessing failure classification (Finding 12) — fixed

`ImageResizeService` now surfaces infrastructure faults (missing/mis-versioned libvips) instead of
swallowing them, keeps the original only for a page it cannot decode, and exposes `VerifyReady`
(probed at worker startup) so a broken worker fails fast. Preprocessing is also server-owned: the
effective options are folded into the server-computed content identity (see the H2 note below), so
a change resets the spool and the worker's engine identity no longer varies with its local config.

## Permanently unreadable repair source consumes the retry budget — fixed

`DistributedUpscaleTaskProcessor` now counts consecutive transient preparation failures per repair
task (`transientPreparationFailures`). A source that reports `PageDifferenceResult.ReadFailed` is
still re-offered on the same 10 s interval, but the streak is incremented on each deferral and the
task is failed once it reaches its `RetryFor` budget, so a permanently unreadable source can no
longer defer for ever. A readable source and every terminal exit clear the streak. The counter is
process-local, matching the repair state beside it and the supported single-replica deployment.

## Residual medium items

- **Permanent local I/O faults burn a chapter per retry (M6).** `ClassifyStreamingFailure` maps every
  `IOException`/`UnauthorizedAccessException` to transient, so a read-only or full worker filesystem
  re-fetches and re-upscales the missing pages up to the restart cap. Direction: distinguish
  `DiskFull`/read-only (terminal) from truly transient I/O (the errno is platform-specific, so this
  needs care).
- **Equal size+mtime source edit can loop (M7).** `Assemble` restarts when the archive changed, but
  `ResolvePageContextAsync` caches descriptors keyed on size+mtime, so a content change preserving
  both never invalidates the cache and the restart repeats to the cap. Direction: fold a cheap
  content signal (or the source's inode/ctime) into the cache key.

## Smaller known items

- `SplitDetectionLayout.Root` is mutable public process-global state that redirects the detector
  paths and the detector identity hash; make it internal/injected so tests can run in parallel.
- `UpscalingDistributionService.Pages` allocates per page in a couple of upload paths and encodes
  detection JSON twice.
- A repair whose missing-page set became empty between preparation and delegation restarts
  (Unavailable) rather than completing, so it can cycle up to the restart cap.
- `RequestTaskRequest.prefetch` / `RequestUpscaleTaskWithHint` are now a no-op hint logged at Debug.
- The engine identity samples three 64 KiB windows per model file, so a same-size weight change
  confined between the windows is not detected.
- Cross-instance mutual exclusion for a chapter is process-local (see `TaskQueue.AcquireChapterGateAsync`);
  a multi-replica deployment needs a DB-level conditional claim. **Single-replica is the supported
  deployment today**: the queue is in-memory and the chapter gate is process-local, so a multi-replica
  deployment can run conflicting chapter tasks. Treat multi-replica as unsupported until a DB-level
  conditional claim (or advisory lock) replaces the gate.
- `IPageSpoolStore` returns the mutable concrete `PageStreamSession` and omits `Remove`/`SweepStale`,
  so it is a test seam rather than a distributed abstraction. Direction: return an opaque handle plus
  an immutable snapshot and move budget/gate access behind store methods (or rename it to say
  "process-local").
- `AtomicFileReplacement` documents a "sibling temp" invariant but does not enforce it; a temp on
  another volume silently degrades the move to a copy.
- `ImageResizeService` format conversion writes new-extension bytes and moves them over the original
  path, so the extension no longer matches the content (only safe because the engine sniffs magic
  bytes).
- Metadata handling uses case-sensitive `.cbz` checks and treats a missing `.cbz` as "no differences"
  (both repair paths now guard `File.Exists` on the source before analysing, so a missing original
  fails terminally instead of rebuilding an empty archive).
- `ArchiveEntryNameGuard.IsSafeEntryName` rejects absolute and Windows drive-absolute (`C:\`, `C:/`) entry
  names but deliberately keeps a drive-relative `C:evil.jpg` or an NTFS alternate-data-stream
  `page.jpg:evil`, because a colon is legal on Linux and rejecting it silently dropped legitimate
  pages. The server never extracts by entry name; a third-party Windows extractor reading a produced
  CBZ could resolve such a name outside the target directory.
- `IPageSpoolStore.WritePageAsync`/`CommitPage` are public but production-unused (test-only), and
  `TryCommitPage`'s `size = 0` default is a budget footgun.
- The streaming inactivity allowance keeps a documented 15-minute floor that the local whole-CBZ
  path does not have, so the two paths kill a wedged engine on different schedules.
- An identity-raced manifest can pair new descriptors with the old identity while the old session is
  still `Assembling`; it self-heals via a `GetPages` `FailedPrecondition` at the cost of extra
  restart round-trips.
- `TouchRoot` is not refreshed during a long single-page upload or assembly, so a sibling replica's
  `SweepStale` could in theory reclaim an in-use spool within the retention window.
- Detection stem-collision handling and detection-upload exception classification are inconsistent
  with the upscale path (both preserve the spool, so safe).
- On host shutdown the in-process upscale worker terminates gracefully via `CancellationToken`.

## Architecture follow-ups (still open)

- Extract a `PageContextResolver` (identity/descriptor/DB/archive work) out of the ~1900-line RPC
  partial and return a result type instead of null-means-transient.
- Decompose `PageStreamClient` (fetch stream / upload queue / progress reporter / work directory) and
  `PageStreamSpool` (session registry / page writer / zip assembler / sweeper).
- A single `TaskQueue.TryBeginChapterTaskAsync(task, onDefer)` owning gate + conflict check + claim
  (the conflict rule itself is now single-sourced in `ChapterConflictGuard`).
- A real engine-fingerprint seam (`IEngineFingerprint`/`IRuntimeInfo`) instead of the static
  `EngineIdentity`; the engine constant and backend are now hashed, but the seam would make them injectable and testable.

## Architecture follow-ups

The page-streaming work rested on duplicated discipline that has largely been extracted. The
remaining items are deliberately deferred.

Done:
1. **`PageManifestBuilder`.** The descriptor builders and identity computers moved out of the RPC
   service into `PageManifestBuilder`. (A `PageIdentity` value object is still a possible refinement.)
2. **One restart/retry taxonomy.** `PageStreamDisposition` + `PageStreamRejections` and a single
   shared `PageStreamRestartException`; the wire `terminal` bool is mapped in one place.
3. **`IPageSpoolStore`.** The store seam exists and states the single-replica constraint once; the
   concrete `PageStreamSpool` is the (process-local) implementation.
4. **`AtomicFileReplacement`.** The "build a sibling temp → atomic move → sweep stale temps" pattern
   is single-sourced for split apply and the streamed-chapter assembly (the repair assembly now uses
   it too).
5. **`RemoteTaskProcessor` lifecycle seam.** `ITaskClaimSource`, `KeepAlivePump` and
   `SoftFailureTracker` make the loop testable without live gRPC.
6. **One finalize pipeline.** `PageStreamFinalizer` owns the complete→assemble→finalize ladder for
   the two upload handlers and the manifest-complete branch.
7. **Typed engine identity + one profile mapper.** Assessed and left as-is. The profile is mapped in
   two places (`ToProtoProfile` on the server, `GetProfileFromResponse` on the worker), but they are
   inverses that *fail loudly* on divergence — the server maps an unknown enum to `Unspecified` and
   the worker throws on `Unspecified` — so the "must agree" hazard is limited. Sharing the protobuf
   types in `MangaIngestWithUpscaling.Shared` would work (compile the proto once with
   `GrpcServices="Both"`, which pulls the ASP.NET Core gRPC server stack into Shared and moves the
   generated types across assemblies), but it would also *remove* a property the integration tests
   currently rely on: the server and client types are generated independently and exercised against
   each other (`remote::` aliases), which is a real wire-compatibility guard. A typed identity value
   object would be a modest readability win at broad churn. Not worth the coupling here.
