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
| `SelectedDeviceIndex` | `Ingest_Upscaler__SelectedDeviceIndex` | `1` | Device index: `0` selects CPU, `1` is the first GPU, `2` the second, and so on. |
| `UseFp16` | `Ingest_Upscaler__UseFp16` | *(auto)* | Use half-precision (FP16) inference. Defaults to auto-detecting hardware capabilities (enabled on modern GPUs, disabled on CPU or unsupported hardware). Set to `true` or `false` to override. |
| `UseCPU` | `Ingest_Upscaler__UseCPU` | `false` | Force CPU inference even when a GPU is available. |
| `TileSize` | `Ingest_Upscaler__TileSize` | `0` | Tile size in pixels for ONNX inference. `0` = auto-estimate the largest tile that fits the VRAM budget (default, derived from the model's activation footprint — see [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md)); `> 0` = manual maximum tile size; `-1` = force single pass (no tiling). |
| `MemoryBudgetBytes` | `Ingest_Upscaler__MemoryBudgetBytes` | `0` | VRAM memory budget in bytes for tile estimation. `0` = auto-detect free VRAM (default). |
| `RecalibrateDeviceMemory` | `Ingest_Upscaler__RecalibrateDeviceMemory` | `false` | Force the one-off per-device memory benchmark to run again on the next upscale. Normally unnecessary: the benchmark runs automatically the first time a device is seen and is cached in `device-memory-profile.json` next to the models. See [Upscaling Memory Model](UPSCALING_MEMORY_MODEL.md#6-per-device-calibration). |

#### `PreferredGpuBackend` values

| Value | Backend |
|---|---|
| `Auto` | Select best available execution provider automatically (default) |
| `WebGPU` | Universal GPU accelerator — Vulkan (Mesa RADV/ANV on Linux), Metal (macOS), D3D12 (Windows) |
| `CUDA` | NVIDIA — CUDA & TensorRT |
| `DirectML` | Windows — DirectX 12 machine learning execution provider |
| `OpenVINO` | Intel — OpenVINO execution provider |
| `MIGraphX` | AMD — MIGraphX execution provider |
| `CPU` | CPU-only fallback |

### Storage settings

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `ModelsDirectory` | `Ingest_Upscaler__ModelsDirectory` | `/models/MangaJaNai` (Docker) | Directory where upscaling models are stored. |

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

Because this is a JSON array, it cannot be expressed as simple environment variables. Override it
by mounting a JSON settings file into the container:

```yaml
    volumes:
      - ./appsettings.override.json:/app/appsettings.override.json
```

`appsettings.override.json`:

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

---

## appsettings.json reference

If you prefer file-based configuration (e.g. when running from source), add an `Upscaler` section
to your `appsettings.json`:

```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CUDA",
    "SelectedDeviceIndex": 1,
    "UseFp16": true,
    "UseCPU": false,
    "ModelsDirectory": "/models/MangaJaNai",
    "RemoteOnly": false,
    "UpscaleTimeout": "00:01:00",
    "MaxDimensionBeforeUpscaling": null,
    "EnableSmartDownscale": false,
    "SmartDownscaleThreshold": 15.0,
    "SmartDownscaleFactor": 0.75,
    "ImageFormatConversionRules": [
      { "FromFormat": ".png",  "ToFormat": ".jpg", "Quality": 98 },
      { "FromFormat": ".avif", "ToFormat": ".jpg", "Quality": 98 }
    ]
  }
}
```

Environment variables always take precedence over `appsettings.json` values.

---

## See Also

- [GPU Backend Configuration](GPU_BACKEND_CONFIGURATION.md) — auto-detection details and supported execution providers
- [Image Format Conversion](IMAGE_FORMAT_CONVERSION.md) — supported formats, quality settings, troubleshooting
- [Smart Downscale](SMART_DOWNSCALE.md) — detecting and correcting cheaply-upscaled source images
- [Upscaling Timeout](UPSCALING_TIMEOUT.md) — per-pixel scaling formula and how to tune it
- [Remote-Only Variant](REMOTE_ONLY_VARIANT.md) — running without local ML dependencies
- [Remote Worker](REMOTE_WORKER.md) — setting up a dedicated upscaling machine
