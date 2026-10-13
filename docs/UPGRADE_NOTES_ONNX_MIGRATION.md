# Upgrading to the ONNX engine (PyTorch removal)

This release replaces the Python/PyTorch upscaler with an in-process ONNX Runtime engine. Most of the
change is internal, but a few things are visible to an existing deployment and are not obvious from the
configuration surface. Nothing here requires a data migration; the database schema is unchanged.

## Container images

* The **`:latest` image now defaults to WebGPU** (`Ingest_Upscaler__PreferredGpuBackend=WebGPU`), where
  it used to default to CUDA. On an NVIDIA host that relied on the previous default, set
  `Ingest_Upscaler__PreferredGpuBackend=CUDA` or move to the `:latest-cuda` image.
  `docs/GPU_BACKEND_CONFIGURATION.md` has the image-to-hardware table.
* The CUDA image moved to **CUDA 13.4**
  (`nvidia/cuda:13.4.2-cudnn-runtime-ubuntu24.04`), which needs a newer NVIDIA driver than the
  previous CUDA 12.x image. On an older driver the container will not start; update the driver or stay
  on the previous release.
* Only **two** flavours are published from this release on: the universal image (WebGPU + CPU) and
  the CUDA image. The `-rocm`, `-xpu` and CUDA-12 tags are **gone**. A deployment pinned to one of
  them (`:latest-rocm`, `:latest-xpu`, …) will keep pulling the last PyTorch-era image, which no
  longer speaks the current worker/server protocol; re-point it at `:latest` (WebGPU — it runs on AMD
  through Vulkan) or `:latest-cuda`.

## Configuration

* `SelectedDeviceIndex = 0` now selects **CPU** — the same rule as `UseCPU = true`, applied wherever
  the value is read. A deployment that set `0` meaning "the first device" now runs on CPU; use `1`
  for the first accelerator.
* `SelectedDeviceIndex` is now a **bound property**, where it used to be a plain field. The
  configuration binder only sets properties, so a value from `appsettings.json` or an `Ingest_`
  environment variable was silently ignored before and now takes effect. A deployment that had `2`
  configured (or a negative value, which the same `<= 0` rule clamps to CPU — `-1` is a `TileSize`
  semantic, not a device index) will behave differently on the next start.
* `UseFp16` now defaults to **`null` (auto-detect)** instead of `true`. Unset means the precision is
  derived from the resolved execution provider and the accelerator's own feature set; set it
  explicitly to pin one. The fp16 and fp32 copies of a model are both downloaded, and the engine
  falls back to the fp32 one when the fp16 one mis-executes.
* `EnableSmartDownscale` now defaults to **`true`** instead of `false`. Images that look like a cheap
  bicubic/bilinear upscale are detected and reduced toward their likely native resolution before AI
  upscaling, so **existing deployments will produce different output** from the first run after the
  upgrade. Set `Ingest_Upscaler__EnableSmartDownscale=false` to keep the previous behaviour, or tune
  `SmartDownscaleThreshold` / `SmartDownscaleFactor`.
  One caveat: the values are now checked at startup, where they used to be checked per image. A file
  that carries a leftover `SmartDownscaleFactor` outside `(0, 1)` — `0` is the common one, and it was
  inert while the feature was disabled — now **fails the boot** instead of being ignored. Fix or
  remove the value, or disable the feature.
* The legacy backend aliases `ROCm`, `ROCm_GFX120X`, `XPU` and `CUDA_12_8` are still accepted and are
  remapped to `WebGPU` / `CUDA`, but the remap is not logged. They carry `[Obsolete]` on the
  `GpuBackend` enum; prefer `WebGPU` / `CUDA` directly.
* Set `PreferredGpuBackend` (and any other `GpuBackend` value) **by name, not by number**: the enum
  was reordered in this release, so a numeric value such as `2` selects a different provider than it
  did before. The names are stable.
* `ShutdownWorkerBeforeSplitDetection` is gone with the worker subprocess it controlled. A value left
  in an existing `appsettings.json` is silently ignored; split detection runs in-process before
  upscaling the chapter, so there is nothing to shut down.
* The PyTorch-era settings — `PythonEnvironmentDirectory`, `WorkerIdleTimeout`,
  `DetectServerRequestTimeout` and the other detect-server/Python-worker options — are **gone**.
  Nothing reads them any more, so a value left in an existing `appsettings.json` is silently ignored
  rather than reported; there is no replacement, because the detector and the upscaler now run
  in-process. Remove them to keep the file honest.

## Splits and detection

* The page-break detector changed from the PyTorch peak-mask algorithm to an ONNX model, and its
  **version was bumped to 2**. Chapters whose splits were detected by the previous algorithm carry
  version 1 and are therefore **re-detected** after the upgrade. Where the new detection finds splits,
  the chapter shows as having pending splits until they are re-applied — the findings come from a
  different algorithm, so they are meant to be reviewed, not inherited. Chapters that need no split
  are re-marked without any action.
* The detector runs its inference in **windows only as far as the memory budget requires**, and in one
  whole-page pass when the page fits. That budget is the same one tile sizing uses (measured free VRAM
  with its safety margins, or an explicit `MemoryBudgetBytes`). Windowing is what bounds the footprint
  of a tall page — a whole-page pass needs roughly 1.5 GiB at 4000 resized rows and 5.6 GiB at 15000
  (the top of the range the model's training strips cover, `STRIP_LENGTH_MAX`) — but it is not free:
  every window re-infers its context rows, so a 2048-row chunk costs about 2.5 times the page's rows.
  The window is therefore sized to the budget, up to the whole page.
* The window's size does **not** affect the result — verified bit-identical against the shipped model
  at chunk sizes 512, 2048, 4096, 8192 and whole-page, on pages of two different kinds (max probability
  difference 0.0, no differing peaks) — so the budget is free to buy the faster whole-page pass. What
  makes a window exact is its context margin, not its length.
* The window carries **1536 rows of context** beyond the rows it is trusted for, deliberately more than
  the reference's 512. Throughout this section *the reference* is the upstream project
  [Lolle2000la/manga-vert-split-nn](https://github.com/Lolle2000la/manga-vert-split-nn) — the model's
  training/calibration code, so `calibrate.py` and `STRIP_LENGTH_MAX` are its files, not this
  repository's. The model's vertical receptive field is 1423 rows: the dilated residual stack
  reaches ±1023 (ten 3x3 blocks with dilations 1, 2, 4, … 512), the smoothing convolution after the
  sigmoid ±8, and the peak test's MaxPool ±400 (kernel 801, i.e. the calibrated `peak_distance` of
  800). A window with less context scores the rows near its edges from a truncated neighbourhood. The
  reference can use 512 because it windows the *logits* and applies the sigmoid, the smoothing and the
  peak search afterwards; this export bakes those three into the graph, so its windows are already
  post-calibration and the margin has to cover the whole receptive field instead. Measured against the
  shipped model on 9000-row pages: with 1536 the windowed result is **bit-identical** to a whole-page
  pass, with 1024 it diverges by 3.8e-6, with 512 by 1.5e-1.
* That also keeps the implementation on the evaluation the baked thresholds were calibrated on: the
  calibration (`calibrate.py`) runs an **uncropped single pass** over whole strips, which is what the
  windowed pass reproduces. The reference's 2048-row window is the model's training crop
  (`--crop_height` default 2048); here it is the fallback when inference runs on the CPU, where a
  VRAM-shaped budget says nothing about the host RAM the pass actually lives in (the budget
  helper's floor exists for GPU tile planning and can never report "nothing measurable"), or when
  nothing could be measured at all.

* Images with **transparency** are now decoded the way the reference decodes them: the alpha band is
  **dropped**, so the colour stored under a transparent pixel is what the detector sees. Previously
  the alpha was composited onto black, which turned a transparent page area into a black band; since
  the detector's score is a row mean, that moved rows across the peak threshold and could drop real
  splits (a fully transparent page area detected as no split at all). Detection results for pages with
  genuine partial transparency can therefore differ after this upgrade — in the direction of the
  upstream reference.
* Two input formats intentionally do **not** reproduce the reference byte for byte, because there the
  reference is the worse of the two: **16-bit** images (PIL's `convert("RGB")` clamps everything from
  255 upwards to white, while libvips scales the full range down correctly — a `I;16` page is unusable
  in the reference) and **CMYK** (libvips converts through the image's profile, PIL through a crude
  fixed transform). 8-bit grayscale, RGB, palette and bilevel pages match the reference exactly.
* If something other than the pinned detector is placed at `<models>/page_break_detector.onnx`, the
  failure is now reported as a clear contract error naming the tensors the file actually has, instead
  of ONNX Runtime's "is not in the metadata" (or, for a differing element type, a bare
  `NullReferenceException`) repeated per page. Replacing the file clears the verdict, because the
  recorded failure is keyed on the file's length and modification time.

## Models

* The model set is re-downloaded in ONNX form. The old PyTorch files are **not** removed: `.pth` and
  `.safetensors` files are left next to the new `.onnx` files in the models directory, together with
  the old `.verified_*` markers. Nothing reads them any more, and deleting them reclaims roughly the
  size of the model set again.
* The `.verified_*` marker format changed, so the first start after the upgrade re-checks every
  package and logs `... changed since it was last verified, re-checking integrity` once per package.
  That is expected, and happens once.

## Behaviour

* Tile sizes are derived from a memory model instead of a fixed 512 px cap, so a page is upscaled in
  fewer, larger tiles. `docs/UPSCALING_MEMORY_MODEL.md` explains the derivation; the practical effect
  is a different (usually faster) grid, not a different output.
* The first upscale of a given model on a given device runs a short memory benchmark (two inferences
  plus two session builds, about a second) and stores the result in `device-memory-profile.json` next
  to the models. It is logged at `Information` and happens once per model per device, not per page.
* The per-session logging of which execution provider a session was built with moved to `Debug`. A
  provider that cannot be attached, or a fall back to CPU, still logs at `Warning`.
