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
* The PyTorch-era settings — `PythonEnvironmentDirectory`, `WorkerIdleTimeout`,
  `DetectServerRequestTimeout` and the other detect-server/Python-worker options — are **gone**.
  Nothing reads them any more, so a value left in an existing `appsettings.json` is silently ignored
  rather than reported; there is no replacement, because the detector and the upscaler now run
  in-process. Remove them to keep the file honest.

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
