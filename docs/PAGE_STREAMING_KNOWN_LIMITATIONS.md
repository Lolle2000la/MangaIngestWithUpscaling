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

## Worker upload backlog (Finding 5)

- **Where:** `PageStreamClient.RunAsync` (`uploads` channel, `OnPageDone`).
- **Current behaviour:** the upload channel is unbounded and `OnPageDone` is a synchronous,
  non-blocking callback, so a fast GPU on a slow link can accumulate a whole extra copy of the
  upscaled chapter on the worker's temp disk.
- **Symptom to watch for:** the worker's temp directory growing by roughly one chapter per in-flight
  task, or disk-full errors under sustained upload backpressure.
- **Direction:** bound the in-flight backlog (bounded channel or semaphore released in the upload
  loop) and pause the fetch/upscale side when it fills, so temp usage is O(page), not O(chapter).

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

## Preprocessing failure classification (Finding 12)

- **Where:** `ImageResizeService` (`PreprocessImageInPlaceAsync`).
- **Current behaviour:** every non-cancellation exception while preprocessing a page is swallowed and
  the original image is kept. That is correct for one undecodable page, but it also masks an
  infrastructure failure (for example libvips missing in the AOT remote worker), and the engine
  identity is computed from the *configured* preprocessing, not whether it ran, so a broken worker
  advertises the same engine as a healthy one.
- **Symptom to watch for:** streamed chapters completing with visibly un-preprocessed pages while
  the local whole-CBZ path (or another worker) applies resizing/format conversion.
- **Direction:** separate decode failures (keep the original) from infrastructure failures (rethrow),
  and add a worker startup readiness probe through `IImageResizeService` so a worker that cannot
  preprocess fails fast instead of producing different bytes.

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
- The engine identity samples three 64 KiB windows per model file, so a same-size weight change
  confined between the windows is not detected.
- Cross-instance mutual exclusion for a chapter is process-local (see `TaskQueue.AcquireChapterGateAsync`);
  a multi-replica deployment needs a DB-level conditional claim.

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
6. **`AtomicFileReplacement`.** The "build a sibling temp → atomic move → sweep stale temps" pattern
   is single-sourced for split apply and the streamed-chapter assembly.
7. **`RemoteTaskProcessor` lifecycle seam.** `ITaskClaimSource`, `KeepAlivePump` and
   `SoftFailureTracker` make the loop testable without live gRPC.

Remaining:
5. **One finalize pipeline.** The manifest-complete path and the two upload handlers still repeat the
   same commit→complete→assemble→finalize ladder with slightly diverging classification; a
   `PageStreamFinalizer` template with per-kind hooks would remove the duplication.
8. **Typed engine identity + one profile mapper.** The identity is still an opaque worker-side
   string and the profile is mapped in two places that must agree. Unifying the mapper needs the
   protobuf types available to the shared project; the server could then optionally verify the
   identity (accept-and-warn first).
