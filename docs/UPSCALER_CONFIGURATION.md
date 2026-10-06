# Upscaler Configuration

This page is the single reference for all upscaler settings.
Because the expected primary deployment is via **Docker / docker-compose**, every setting is shown
first as an **environment variable** and then as the equivalent `appsettings.json` key.

> **See also:** detailed deep-dives in
> [GPU Backend Configuration](GPU_BACKEND_CONFIGURATION.md) ·
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
      Ingest_Upscaler__PreferredGpuBackend: CUDA    # NVIDIA CUDA
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
      Ingest_Upscaler__PreferredGpuBackend: Auto
      Ingest_Upscaler__UseFp16: "true"
      Ingest_Upscaler__SelectedDeviceIndex: "1"
    devices:
      - /dev/dri # GPU access for Vulkan (Mesa RADV / ANV)
```

### CPU-only (no GPU)

```yaml
    environment:
      Ingest_Upscaler__PreferredGpuBackend: CPU
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
| `UseFp16` | `Ingest_Upscaler__UseFp16` | `true` | Use half-precision (FP16) inference. Recommended for modern GPUs; turn off for CPU or older hardware. |
| `UseCPU` | `Ingest_Upscaler__UseCPU` | `false` | Force CPU inference even when a GPU is available. |
| `TileSize` | `Ingest_Upscaler__TileSize` | `0` | Tile size in pixels for ONNX inference. `0` = auto-estimate from model architecture and VRAM (default); `> 0` = manual tile size; `-1` = force single pass (no tiling). |
| `MemoryBudgetBytes` | `Ingest_Upscaler__MemoryBudgetBytes` | `0` | VRAM memory budget in bytes for tile estimation. `0` = auto-detect free VRAM (default). |

#### `PreferredGpuBackend` values

| Value | Backend |
|---|---|
| `Auto` | Select best available execution provider automatically (default) |
| `CUDA` | NVIDIA — CUDA & TensorRT |
| `WebGPU` | AMD / Intel / Universal — WebGPU via Mesa RADV / ANV Vulkan |
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
| `EnableSmartDownscale` | `Ingest_Upscaler__EnableSmartDownscale` | `false` | Detect and downscale cheaply-upscaled images before AI upscaling — see [Smart Downscale](SMART_DOWNSCALE.md). |
| `SmartDownscaleThreshold` | `Ingest_Upscaler__SmartDownscaleThreshold` | `15.0` | Laplacian std-dev below which an image is considered cheaply upscaled. Lower = stricter; higher = more aggressive. |
| `SmartDownscaleFactor` | `Ingest_Upscaler__SmartDownscaleFactor` | `0.75` | Fallback scale factor (e.g. `0.75` = 75 %) used when the FFT cliff detector finds no clear cutoff frequency. |
| `DetectServerRequestTimeout` | `Ingest_Upscaler__DetectServerRequestTimeout` | `00:10:00` | Maximum time a single request to the resident split-detection server may run before it is cancelled and the server killed; the caller then falls back to the per-image CLI, so a wedged detector cannot hang a task. `0` disables the guard. |

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
| `RemoteOnly` | `Ingest_Upscaler__RemoteOnly` | `false` | Disable local upscaling entirely; all tasks are forwarded to remote workers. Also suppresses Python environment setup. |
| `ForceAcceptExistingEnvironment` | `Ingest_Upscaler__ForceAcceptExistingEnvironment` | `false` | Skip version and backend checks and use the existing Python environment as-is. Useful for air-gapped systems with a manually provisioned environment. |

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
    "PythonEnvironmentDirectory": "/data/pyenv",
    "RemoteOnly": false,
    "ForceAcceptExistingEnvironment": false,
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

- [GPU Backend Configuration](GPU_BACKEND_CONFIGURATION.md) — auto-detection details, PyTorch version matrix, environment recreation logic
- [Image Format Conversion](IMAGE_FORMAT_CONVERSION.md) — supported formats, quality settings, troubleshooting
- [Smart Downscale](SMART_DOWNSCALE.md) — detecting and correcting cheaply-upscaled source images
- [Upscaling Timeout](UPSCALING_TIMEOUT.md) — per-pixel scaling formula and how to tune it
- [Remote-Only Variant](REMOTE_ONLY_VARIANT.md) — running without local ML dependencies
- [Remote Worker](REMOTE_WORKER.md) — setting up a dedicated upscaling machine
