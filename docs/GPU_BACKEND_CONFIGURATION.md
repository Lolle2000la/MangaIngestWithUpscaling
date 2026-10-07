# GPU Backend Configuration & Docker Flavors

> For a complete overview of all upscaler settings in one place, see
> [Upscaler Configuration](UPSCALER_CONFIGURATION.md).

MangaIngestWithUpscaling uses a pure C# machine learning inference engine powered by **Microsoft.ML.OnnxRuntime**. There is no Python environment, no PyTorch virtual environment, and no runtime pip wheel downloads required.

Hardware acceleration is achieved through native ONNX **Execution Providers (EP)** packaged inside two dedicated Docker images:

---

## Docker Image Flavors

| Image Tag | Execution Provider | Target Hardware | Requirements |
|---|---|---|---|
| `:latest` / `:latest-dev` | **WebGPU** (Vulkan via Mesa RADV / ANV) + CPU fallback | AMD Radeon (RX 5000/6000/7000/9000), Intel Arc / Iris Xe, CPU fallback | Host GPU device access (`/dev/dri`) |
| `:latest-cuda` / `:latest-dev-cuda` | **CUDA** & **TensorRT** | NVIDIA GeForce GTX/RTX, Quadro, Tesla | NVIDIA driver + `nvidia-container-toolkit` |

Both flavor tags are published for both the main web application (`manga-ingest-with-upscaling`) and the standalone remote worker (`manga-ingest-with-upscaling-remote-worker`).

---

## Configuration

Upscaler hardware settings can be configured via environment variables or `appsettings.json`:

### `PreferredGpuBackend`

Controls which execution provider is attempted:

| Value | Description |
|---|---|
| `Auto` *(default)* | Automatically selects the best available accelerator for your image and platform (CUDA → WebGPU → CPU on Linux) |
| `CUDA` | Forces NVIDIA CUDA / TensorRT execution provider (in `:latest-cuda`) |
| `WebGPU` | Forces WebGPU execution provider (Mesa RADV Vulkan on Linux, Direct3D 12 on Windows) |
| `CPU` | Forces CPU execution provider |

Environment variable:
```bash
export Ingest_Upscaler__PreferredGpuBackend=Auto
```

### `SelectedDeviceIndex`

Index of the device to use:
- `0`: CPU
- `1`: First GPU (default)
- `2`: Second GPU (for multi-GPU systems)

Environment variable:
```bash
export Ingest_Upscaler__SelectedDeviceIndex=1
```

### `UseFp16`

Enables half-precision (FP16) inference:
- *(unset / null, default)*: Automatically detects hardware capability (enabled for modern GPUs, disabled on CPU or unsupported hardware)
- `true`: Forces half-precision FP16 models
- `false`: Forces single-precision FP32 models

Environment variable:
```bash
export Ingest_Upscaler__UseFp16=true
```

### `UseCPU`

Forces CPU execution regardless of hardware:
- `false` *(default)*
- `true`: Skips all GPU execution providers and uses CPU

Environment variable:
```bash
export Ingest_Upscaler__UseCPU=false
```

---

## Docker Compose Examples

### Universal Image (AMD Radeon / Intel / CPU)

The standard `:latest` image accelerates inference via WebGPU / Vulkan on AMD Radeon and Intel GPUs, with clean CPU fallback if no GPU is available:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin
      Ingest_Upscaler__PreferredGpuBackend: Auto
      Ingest_Upscaler__SelectedDeviceIndex: 1
      Ingest_Upscaler__UseFp16: true
    volumes:
      - /path/to/store/appdata:/data
      - /path/to/store/models:/models
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080
      - 8081:8081
    devices:
      - /dev/dri # GPU access for Vulkan (Mesa RADV / ANV)
```

### NVIDIA GPU (CUDA)

Use the `:latest-cuda` (or `:latest-dev-cuda`) image for native NVIDIA CUDA & TensorRT acceleration:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-cuda
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin
      Ingest_Upscaler__PreferredGpuBackend: CUDA
      Ingest_Upscaler__SelectedDeviceIndex: 1
      Ingest_Upscaler__UseFp16: true
    volumes:
      - /path/to/store/appdata:/data
      - /path/to/store/models:/models
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
```

---

## Automatic Fallback & Troubleshooting

If a configured execution provider cannot be initialized (for instance, if drivers are missing or incompatible), the application will:
1. Log a descriptive warning explaining why the accelerator failed to initialize.
2. Automatically fall back to CPU execution.
3. Continue processing tasks without crashing or dropping jobs.
