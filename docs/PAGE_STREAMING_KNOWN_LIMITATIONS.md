# Page streaming — known limitations and investigation leads

This page records behaviour that is known, deliberately scoped, or only safe under an assumption.
None of it is known to corrupt data today, but each item lists the symptom to watch for, where the
relevant code lives, and the direction a fix should take, so a future failure can be traced back to
its cause quickly.

The page-streaming design itself is described in [REMOTE_WORKER.md](REMOTE_WORKER.md).

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
- `DetectServerClient` releases a GPU-cache request that can wait the full timeout when there is no
  server to ask (now returns early) and its cancellation-ack handshake can skip killing a busy
  server.
- `UpscalingDistributionService.Pages` allocates per page in a couple of upload paths and encodes
  detection JSON twice.
- A repair whose missing-page set became empty between preparation and delegation restarts
  (Unavailable) rather than completing, so it can cycle up to the restart cap.
- `RequestTaskRequest.prefetch` / `RequestUpscaleTaskWithHint` are now a no-op hint logged at Debug.
- The detector logs a warning per page while the resident server is in its cooldown window.
- Device selection for the resident split detector is derived from `SelectedDeviceIndex`, but `GpuBackend`
  has no MPS value: on an Apple MPS host the backend resolves to `Auto`, so `--device` is omitted and
  the detector falls back to its own auto-select (which may not match the upscaler's device).
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
- `PageStreamSpool.IsSafeEntryName` rejects absolute and Windows drive-absolute (`C:\`, `C:/`) entry
  names but deliberately keeps a drive-relative `C:evil.jpg` or an NTFS alternate-data-stream
  `page.jpg:evil`, because a colon is legal on Linux and rejecting it silently dropped legitimate
  pages. The server never extracts by entry name; a third-party Windows extractor reading a produced
  CBZ could resolve such a name outside the target directory.
- `IPageSpoolStore.WritePageAsync`/`CommitPage` are public but production-unused (test-only), and
  `TryCommitPage`'s `size = 0` default is a budget footgun.
- The streaming inactivity allowance keeps a documented 15-minute floor that the local whole-CBZ
  path does not have, so the two paths kill a wedged engine on different schedules.
- A permanently unreadable source (`ReadFailed`) is re-offered every 10 s and never consumes the
  retry budget, so `RetryFor` never escalates; a separate transient-attempt counter is needed.
- An identity-raced manifest can pair new descriptors with the old identity while the old session is
  still `Assembling`; it self-heals via a `GetPages` `FailedPrecondition` at the cost of extra
  restart round-trips.
- `TouchRoot` is not refreshed during a long single-page upload or assembly, so a sibling replica's
  `SweepStale` could in theory reclaim an in-use spool within the retention window.
- Detection stem-collision handling and detection-upload exception classification are inconsistent
  with the upscale path (both preserve the spool, so safe).
- On host shutdown the worker is stopped by best-effort NDJSON `cancel`/`shutdown` commands; a failed
  `cancel` send is now logged at Information instead of Debug, so a lost stop is visible. The
  terminal's SIGINT also reaches the child directly, and the worker now handles SIGINT/SIGTERM
  gracefully (submodule `9e0a956`).

## Architecture follow-ups (still open)

- Extract a `PageContextResolver` (identity/descriptor/DB/archive work) out of the ~1900-line RPC
  partial and return a result type instead of null-means-transient.
- Decompose `PageStreamClient` (fetch stream / upload queue / progress reporter / work directory) and
  `PageStreamSpool` (session registry / page writer / zip assembler / sweeper).
- A generic `NdjsonJobRegistry<TResult>` and a `FailureCooldown` value type so `DetectServerClient`
  and `MangaJaNaiWorkerClient` stop duplicating job/TCS/cooldown plumbing.
- A single `TaskQueue.TryBeginChapterTaskAsync(task, onDefer)` owning gate + conflict check + claim
  (the conflict rule itself is now single-sourced in `ChapterConflictGuard`).
- A real engine-fingerprint seam (`IEngineFingerprint`/`IRuntimeInfo`) instead of the static
  `EngineIdentity` plus `PythonService.Environment` reach-through; the runtime version and engine
  constant are now hashed, but the seam would make them injectable and testable.

## Architecture follow-ups

The page-streaming work rested on duplicated discipline that has largely been extracted. The
remaining items are deliberately deferred.

Done:
1. **Shared resident NDJSON process supervisor.** Extracted to `ResidentNdjsonProcess` (plus a shared
   `StderrTailBuffer`); `MangaJaNaiWorkerClient` and `DetectServerClient` are protocol adapters.
2. **`PageManifestBuilder`.** The descriptor builders and identity computers moved out of the RPC
   service into `PageManifestBuilder`. (A `PageIdentity` value object is still a possible refinement.)
3. **One restart/retry taxonomy.** `PageStreamDisposition` + `PageStreamRejections` and a single
   shared `PageStreamRestartException`; the wire `terminal` bool is mapped in one place.
4. **`IPageSpoolStore`.** The store seam exists and states the single-replica constraint once; the
   concrete `PageStreamSpool` is the (process-local) implementation.
5. **`AtomicFileReplacement`.** The "build a sibling temp → atomic move → sweep stale temps" pattern
   is single-sourced for split apply and the streamed-chapter assembly (the repair assembly now uses
   it too).
6. **`RemoteTaskProcessor` lifecycle seam.** `ITaskClaimSource`, `KeepAlivePump` and
   `SoftFailureTracker` make the loop testable without live gRPC.
7. **One finalize pipeline.** `PageStreamFinalizer` owns the complete→assemble→finalize ladder for
   the two upload handlers and the manifest-complete branch.
8. **Typed engine identity + one profile mapper.** Assessed and left as-is. The profile is mapped in
   two places (`ToProtoProfile` on the server, `GetProfileFromResponse` on the worker), but they are
   inverses that *fail loudly* on divergence — the server maps an unknown enum to `Unspecified` and
   the worker throws on `Unspecified` — so the "must agree" hazard is limited. Sharing the protobuf
   types in `MangaIngestWithUpscaling.Shared` would work (compile the proto once with
   `GrpcServices="Both"`, which pulls the ASP.NET Core gRPC server stack into Shared and moves the
   generated types across assemblies), but it would also *remove* a property the integration tests
   currently rely on: the server and client types are generated independently and exercised against
   each other (`remote::` aliases), which is a real wire-compatibility guard. A typed identity value
   object would be a modest readability win at broad churn. Not worth the coupling here.
