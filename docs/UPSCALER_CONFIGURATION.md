# Upscaler Configuration

This page is the single reference for all upscaler settings.
Because the expected primary deployment is via **Docker / docker-compose**, every setting is shown
first as an **environment variable** and then as the equivalent `appsettings.json` key.

> **See also:** detailed deep-dives in
> [GPU Backend Configuration](GPU_BACKEND_CONFIGURATION.md) ·
> [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md) ·
> [Image Format Conversion](IMAGE_FORMAT_CONVERSION.md) ·
> [Smart Downscale](SMART_DOWNSCALE.md) ·
> [Upscaling Timeout](UPSCALING_TIMEOUT.md) ·
> [Remote-Only Variant](REMOTE_ONLY_VARIANT.md)

---

## Quick-start docker-compose snippets

### NVIDIA GPU (CUDA)

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-cuda
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin                              # your timezone
      Ingest_Upscaler__UseFp16: "true"              # recommended for modern GPUs
      Ingest_Upscaler__SelectedDeviceIndex: "1"     # device index (0 = CPU, 1 = first GPU)
    volumes:
      - ./data:/data       # database and logs
      - ./models:/models   # upscaling models
      - ./ingest:/ingest
      - ./library:/library
    ports:
      - "8080:8080"
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
```

### Universal (AMD Radeon / Intel / CPU)

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      Ingest_Upscaler__UseFp16: "true"
      Ingest_Upscaler__SelectedDeviceIndex: "1"
    devices:
      - /dev/dri # GPU access for Vulkan (Mesa RADV / ANV)
```

### CPU-only (no GPU)

```yaml
    environment:
      Ingest_Upscaler__UseCPU: "true"
```

### Remote-only (delegate all upscaling to a remote worker)

```yaml
    environment:
      Ingest_Upscaler__RemoteOnly: "true"
    ports:
      - "8080:8080"
      - "8081:8081"   # gRPC — remote workers connect here
```

---

## Complete settings reference

The environment variable for each setting follows the ASP.NET Core convention:
`Ingest_Upscaler__<PropertyName>` (double underscore between segments).

### GPU & hardware settings

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `PreferredGpuBackend` | `Ingest_Upscaler__PreferredGpuBackend` | `Auto` | Which GPU execution provider to use. See [values](#preferredgpubackend-values). |
| `SelectedDeviceIndex` | `Ingest_Upscaler__SelectedDeviceIndex` | `1` | Device index: `0` selects CPU, `1` is the first GPU, `2` the second, and so on. The index is resolved against the *Vulkan* device list for the memory budget and against the execution provider's own list for inference, which agree on a single-GPU host; see the known limitation in [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md). |
| `UseFp16` | `Ingest_Upscaler__UseFp16` | *(auto)* | Use half-precision (FP16) inference. Defaults to auto-detecting hardware capabilities (enabled on modern GPUs, disabled on CPU or unsupported hardware). Set to `true` or `false` to override. With `Auto`/`true` on the WebGPU provider, the fp32 copies of the models are downloaded alongside the fp16 ones as a fallback for the architectures WebGPU cannot run in fp16 — see [GPU Backend Configuration](GPU_BACKEND_CONFIGURATION.md#black-pages). |
| `UseCPU` | `Ingest_Upscaler__UseCPU` | `false` | Force CPU inference even when a GPU is available. |
| `TileSize` | `Ingest_Upscaler__TileSize` | `0` | Tile size in pixels for ONNX inference. `0` = plan the grid from the model's activation footprint and the VRAM budget (default — see [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md)); `> 0` = manual maximum tile size; `-1` = force single pass (no tiling). |
| `MemoryBudgetBytes` | `Ingest_Upscaler__MemoryBudgetBytes` | *(unset)* | VRAM memory budget for tile estimation, as a byte count or a size string (`4GiB`, `12288MiB`, `4294967296`). Unset means auto-detect free VRAM (default); a value that cannot be parsed fails startup. |
| `VramUtilizationFraction` | `Ingest_Upscaler__VramUtilizationFraction` | *(unset)* | Fraction of free VRAM (`0.0`–`1.0`) to budget when auto-detecting. Unset means adaptive (~97% headless, ~95% with a desktop attached). Set `1.0` on a dedicated host. Used for the tile budget *and* the memory benchmark, so it also caps what the benchmark may allocate. |
| `VramSafetyMarginBytes` | `Ingest_Upscaler__VramSafetyMarginBytes` | *(unset)* | Safety margin subtracted from free VRAM, as a byte count or a size string (`512MiB`, `1.5 GiB`). Unset means automatic (256 MiB headless, 1.5–3.0 GiB with a desktop attached). A value that cannot be parsed fails startup. |
| `VramExclusiveThresholdBytes` | `Ingest_Upscaler__VramExclusiveThresholdBytes` | `350 MiB` | Usage at or below which the card is treated as quasi-exclusive (minimal 256 MiB reserve), as a byte count or a size string (`700MiB`, `1 GiB`). Raise it when another process reliably shares the GPU but stays bounded — e.g. a video transcoder holding 700 MiB: at `1GiB` the budget again treats the card as exclusive. A value that cannot be parsed fails startup, like the other size settings; the default applies only while the setting is unset. See [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md). |
| `RecalibrateDeviceMemory` | `Ingest_Upscaler__RecalibrateDeviceMemory` | `false` | Force the one-off per-device memory benchmark to run again on the next upscale. Normally unnecessary: the benchmark runs automatically the first time a device is seen and is cached in `device-memory-profile.json` next to the models. See [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md#6-per-model-calibration-per-device). |

#### `PreferredGpuBackend` values

| Value | Backend |
|---|---|
| `Auto` | Select best available execution provider automatically (default) |
| `WebGPU` | Universal GPU accelerator — Vulkan (Mesa RADV/ANV on Linux), Metal (macOS), D3D12 (Windows) |
| `CUDA` | NVIDIA — CUDA |
| `DirectML` | Windows — DirectX 12 machine learning execution provider |
| `OpenVINO` | Intel — OpenVINO execution provider |
| `MIGraphX` | AMD — MIGraphX execution provider |
| `CPU` | CPU-only fallback |

### Storage settings

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `ModelsDirectory` | `Ingest_Upscaler__ModelsDirectory` | `/models/MangaJaNai` (Docker) | Directory where upscaling models are stored. |

#### Model integrity and the verification marker

Every downloaded model package is verified against the SHA-256 hashes pinned in the application, and a
marker file `.verified_<zipHash>` is written next to the models once that verification passes. The
marker does **not** mean "trust these files blindly": it stores each model file's size and last-write
time, and on every startup those fingerprints are compared with the files on disk.

- **Files unchanged** (size and mtime match the marker): the SHA-256 pass is skipped, so a models
  directory holding several gigabytes does not get re-hashed on every start.
- **Any file changed** (different size or mtime — e.g. corrupted, truncated, or swapped after the
  first download): every file in the package is re-hashed before the package is used. A mismatch
  triggers a re-download instead of silently loading a broken model.
- **Legacy markers** written by older versions contain only the bare zip hash. They are treated as
  stale: the package is re-verified once and the marker is rewritten in the fingerprint format.

Residual trust boundary: a modification that preserves **both** the file size and its mtime (for
example, deliberately restoring the original timestamp after editing bytes in place, or silent bit
rot that never updates metadata) is not detected without re-hashing. Re-run with an empty models
directory, or delete the `.verified_*` markers, to force a full re-verification if you suspect
tampering that deliberately preserved metadata.

### Model selection

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `PreferredModelArchitectures` | `Ingest_Upscaler__PreferredModelArchitectures` | `FDAT_M,ESRGAN,SPAN,FDAT_XL,DAT2,HAT_L` | Order in which model architectures are preferred when several are available for the same scale, most preferred first, as a comma-separated list. An unrecognised name fails startup. |

The recognised names are `ESRGAN`, `SPAN`, `FDAT_M`, `FDAT_XL`, `DAT2` and `HAT_L` (case-insensitive;
`FDAT_M`/`FDATM` and `FDAT_XL`/`FDATXL` are equivalent). Architectures the value does not name keep
their built-in relative order *after* the listed ones, so a partial list re-ranks without dropping
models — naming one architecture cannot lock the others out.

The default puts the fast, GPU-native architectures ahead of the transformers, which are
considerably slower. The most useful thing to change is promoting `FDAT_XL`, which otherwise sits
behind `FDAT_M`, `ESRGAN` and `SPAN` and is only reached when all three are missing:

```yaml
environment:
  - Ingest_Upscaler__PreferredModelArchitectures=FDAT_XL,FDAT_M,ESRGAN,SPAN,DAT2,HAT_L
```

Two caveats. The **model version** order (V3detail, V3denoise, V2standard, V1) is *not* configurable
and stays the outer one, so a promoted architecture is preferred **within** each version: with
`FDAT_XL` first, the order is V3detail's `FDAT_XL`, then V3detail's `FDAT_M`, …, then V3denoise's
`FDAT_XL`. And this changes the *preference*, not the fallback: a model the execution provider cannot
run is still skipped for the next candidate, so a promoted architecture that fails everywhere costs
one probe per page, not a failure.

### Preprocessing settings

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `MaxDimensionBeforeUpscaling` | `Ingest_Upscaler__MaxDimensionBeforeUpscaling` | *(disabled)* | Downscale images so that neither width nor height exceeds this value before upscaling. Helps limit VRAM usage. Leave unset or set to `0` to disable. |
| `UpscaleTimeout` | `Ingest_Upscaler__UpscaleTimeout` | `00:01:00` | Per-million-pixel inactivity timeout (`hh:mm:ss`), scaled by the largest image in the archive — see [Upscaling Timeout](UPSCALING_TIMEOUT.md). |
| `EnableSmartDownscale` | `Ingest_Upscaler__EnableSmartDownscale` | `true` | Detect and downscale cheaply-upscaled images before AI upscaling — see [Smart Downscale](SMART_DOWNSCALE.md). |
| `SmartDownscaleThreshold` | `Ingest_Upscaler__SmartDownscaleThreshold` | `15.0` | Laplacian std-dev below which an image is considered cheaply upscaled. Lower = stricter; higher = more aggressive. |
| `SmartDownscaleFactor` | `Ingest_Upscaler__SmartDownscaleFactor` | `0.75` | Fallback scale factor (e.g. `0.75` = 75 %) used when the FFT cliff detector finds no clear cutoff frequency. |

```yaml
    environment:
      Ingest_Upscaler__MaxDimensionBeforeUpscaling: "2048"
      Ingest_Upscaler__UpscaleTimeout: "00:02:00"   # 2 min/MP for a slow GPU
```

#### Image format conversion rules

The `ImageFormatConversionRules` setting converts image formats during preprocessing (before
upscaling). The original files are never modified.

**Default behaviour:** PNG and AVIF images are converted to JPG at quality 98 for upscaler
compatibility. This can be disabled or overridden.

Because this is a JSON array, it cannot be expressed as simple environment variables. The intended
override is a mounted `appsettings.override.json`, but the container does **not** currently load one
— nothing in the app calls `AddJsonFile` for it, so mounting that file has no effect. Set the rules
in the image's own `appsettings.json` instead (or run from source):

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": [
      { "FromFormat": ".png",  "ToFormat": ".jpg", "Quality": 98 },
      { "FromFormat": ".avif", "ToFormat": ".jpg", "Quality": 98 }
    ]
  }
}
```

Set to an empty array (`[]`) to disable all format conversion.

See [Image Format Conversion](IMAGE_FORMAT_CONVERSION.md) for a full reference including
supported formats and use-case examples.

### Operational settings

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `RemoteOnly` | `Ingest_Upscaler__RemoteOnly` | `false` | Disable local upscaling entirely; all tasks are forwarded to remote workers. Also suppresses local model downloading. |

### Page-streaming settings

These apply to the streamed remote-worker path (see [Page Streaming](PAGE_STREAMING_KNOWN_LIMITATIONS.md)).

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `SpoolDirectory` | `Ingest_Upscaler__SpoolDirectory` | *(system temp)* | App-owned directory the page spool is written under. The app creates `<pid>-<guid>` roots and sweeps only those, so do not point it at a directory with unrelated data. Set it to a path on a real disk when `/tmp` is a RAM-backed tmpfs. |
| `MaxSpoolBytesPerTask` | `Ingest_Upscaler__MaxSpoolBytesPerTask` | `8 GiB` | Upper bound on the bytes spooled for one streamed task, as a byte count or a size string. There is no process-wide cap. Note that `"0"` parses to 0 and then falls back to the 8 GiB default. |
| `ManifestTimeout` | `Ingest_Upscaler__ManifestTimeout` | `00:10:00` | How long a worker waits for a page manifest. Raise it when a large chapter on slow storage has to be assembled server-side before the manifest is answered. |

---

## appsettings.json reference

If you prefer file-based configuration (e.g. when running from source), add an `Upscaler` section
to your `appsettings.json`:

```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CUDA",
    "SelectedDeviceIndex": 1,
    "UseFp16": null,
    "UseCPU": false,
    "ModelsDirectory": "/models/MangaJaNai",
    "PreferredModelArchitectures": "FDAT_XL,FDAT_M,ESRGAN,SPAN,DAT2,HAT_L",
    "RemoteOnly": false,
    "UpscaleTimeout": "00:01:00",
    "MaxDimensionBeforeUpscaling": null,
    "EnableSmartDownscale": true,
    "SmartDownscaleThreshold": 15.0,
    "SmartDownscaleFactor": 0.75,
    "ImageFormatConversionRules": [
      { "FromFormat": ".png",  "ToFormat": ".jpg", "Quality": 98 },
      { "FromFormat": ".avif", "ToFormat": ".jpg", "Quality": 98 }
    ]
  }
}
```

`UseFp16: null` is the default (`Auto`): the hardware capability decides, exactly as in the table
above. `EnableSmartDownscale` now defaults to `true`; it defaulted to `false` before this release.

Environment variables always take precedence over `appsettings.json` values.

---

## Upgrading from the Python workflow

The deleted Python worker shipped its base behaviour in `appstate2.json`. Two of its post-processing
behaviours were **not** carried over to the ONNX engine, so output changes on upgrade:

- **`AutoAdjustLevels: true`** (automatic levels adjustment on the ESRGAN chains) is gone; there is
  no equivalent setting.
- **A 3840×2160 post-upscale resize** (`ResizeWidthAfterUpscale` / `ResizeHeightAfterUpscale`) is
  gone; output keeps the upscaled resolution.

Both belonged to the removed Python backend, not to the ONNX model set.

---

## See Also

- [GPU Backend Configuration](GPU_BACKEND_CONFIGURATION.md) — auto-detection details and supported execution providers
- [Image Format Conversion](IMAGE_FORMAT_CONVERSION.md) — supported formats, quality settings, troubleshooting
- [Smart Downscale](SMART_DOWNSCALE.md) — detecting and correcting cheaply-upscaled source images
- [Upscaling Timeout](UPSCALING_TIMEOUT.md) — per-pixel scaling formula and how to tune it
- [Remote-Only Variant](REMOTE_ONLY_VARIANT.md) — running without local ML dependencies
- [Remote Worker](REMOTE_WORKER.md) — setting up a dedicated upscaling machine
