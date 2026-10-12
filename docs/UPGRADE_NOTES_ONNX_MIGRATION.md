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
* The detector now runs **one inference over the whole resized page** (width 768, height scaled to
  keep the aspect ratio). The upstream reference (`manga-vert-split-nn`'s `detect_breaks.py`) instead
  windowed the inference — chunks of 2048 rows with 512 rows of overlap — because its PyTorch pipeline
  averaged raw logits and only then applied the sigmoid, the smoothing and the peak search. The ONNX
  export this app uses has those three steps **inside the graph** (`export_onnx.py` wraps
  `predict(apply_calibration=True)`), which makes its output a function of the whole sequence rather
  than of a row's neighbourhood: measured against the shipped model, identical rows give different
  probabilities (max difference 0.94) in a 2048-row and a 4000-row input, with peak counts of 3 and 1.
  Windowing that graph is therefore *not* equivalent to a single pass, and single pass is what keeps
  detection faithful to the calibrated model.
* The practical consequence is memory: the detector's activations grow with the resized height —
  measured at roughly **1.5 GiB for a 4000-row page** (against ~100 MiB for one 3072-row window).
  Ordinary pages (a manga page at 2:1 or 3:2 aspect resizes to well under 2000 rows) are far from
  that. Very tall pages — long webtoon strips — can exhaust device memory during detection; the
  chapter is then retried and, if the failure repeats, fails with a memory error. Recovering that
  case properly needs a logits-only export plus the smoothing and peak search reimplemented in C#;
  it is not something that can be fixed by chunking this graph.

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
