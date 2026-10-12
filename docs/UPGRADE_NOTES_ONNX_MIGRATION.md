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
* The legacy backend aliases `ROCm`, `ROCm_GFX120X`, `XPU` and `CUDA_12_8` are still accepted and are
  remapped to `WebGPU` / `CUDA`, but the remap is not logged. They carry `[Obsolete]` on the
  `GpuBackend` enum; prefer `WebGPU` / `CUDA` directly.

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
