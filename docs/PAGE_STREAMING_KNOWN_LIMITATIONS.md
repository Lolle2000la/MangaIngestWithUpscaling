# Page streaming — known limitations and investigation leads

This page records behaviour that is known, deliberately scoped, or only safe under an assumption.
None of it is known to corrupt data today, but each item lists the symptom to watch for, where the
relevant code lives, and the direction a fix should take, so a future failure can be traced back to
its cause quickly.

The page-streaming design itself is described in [REMOTE_WORKER.md](REMOTE_WORKER.md).

## Spool location and budget (Finding 3)

- **Where:** `PageStreamSpool` (`SpoolRoot`, `MaxTaskBytes`).
- **Current behaviour:** the spool root is always under `Path.GetTempPath()`, the per-task budget is
  a fixed 8 GiB, and there is no process-wide cap.
- **Symptom to watch for:** a container whose `/tmp` is a tmpfs (RAM-backed) exhausting host memory
  when several large chapters spool at once; a single task filling the spool volume.
- **Direction:** make the root configurable (an `UpscalerConfig` option), document that it must not
  be tmpfs, and add a global (process-wide) budget in addition to the per-task one.

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

- **Where:** `PageStreamClient.ManifestTimeout` and the manifest-complete branch in
  `UpscalingDistributionService.Pages` (which assembles the CBZ inline before replying).
- **Current behaviour:** the client allows 2 minutes for `GetPageManifest`; when the chapter is
  already fully spooled the server assembles the final CBZ inside that call, which can exceed the
  deadline on slow storage and a large chapter. A `DeadlineExceeded` is transient, so the worker
  requeues and the assembly is retried (and can eventually fail the chapter).
- **Symptom to watch for:** a large already-spooled chapter repeatedly re-streaming or failing with
  deadline errors, with assembly log lines spanning more than the manifest timeout.
- **Direction:** separate "the chapter is complete" from "the CBZ has been assembled": answer the
  manifest immediately and finalize asynchronously (the worker returns and the server completes the
  task), or give the complete/assembly path an explicit, larger budget.

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

The page-streaming work currently rests on duplicated discipline that would benefit from extraction.
These are deliberately deferred to keep this PR reviewable; each is independent.

1. **Shared resident NDJSON process supervisor.** `MangaJaNaiWorkerClient` and `DetectServerClient`
   independently implement the same spawn/ready/watchdog/line-protocol/kill machinery. Extract a
   `ResidentNdjsonProcess`; each client becomes a thin protocol adapter.
2. **`PageManifestBuilder` + `PageIdentity`.** The RPC service re-walks the archive and rebuilds the
   identity in three near-identical copies; one archive-walk routine plus a value object removes it.
3. **One restart/retry taxonomy.** The retry disposition is derived in three places (the wire bool,
   the server's return sites, and `ClassifyStreamingFailure`), with two different
   `PageStreamRestartException` types. A shared `Disposition` mapping makes it explicit and testable.
4. **`IPageSpoolStore`.** The process-local / pin-to-one-replica constraint leaks into five handlers;
   a store abstraction states it once and makes multi-replica a swap-in adapter.
5. **One finalize pipeline.** The manifest-complete path and the two upload handlers repeat the same
   commit→complete→assemble→finalize ladder with slightly diverging classification.
6. **`CbzBuilder` + `IAtomicFileReplacer`.** "Build a sibling temp → atomic move → sweep stale temps"
   is implemented in the spool assembly, the streamed chapter assembly, and split apply.
7. **`RemoteTaskProcessor` lifecycle seam.** `ITaskClaimSource`, a single keep-alive pump, and a
   soft-failure tracker make the loop testable without live gRPC.
8. **Typed engine identity + one profile mapper.** The identity is an opaque worker-side string and
   the profile is mapped in two places that must agree; let the server optionally verify it and
   share one mapper.
