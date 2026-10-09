# GPU Backend Configuration & Docker Flavors

> For a complete overview of all upscaler settings in one place, see
> [Upscaler Configuration](UPSCALER_CONFIGURATION.md).

MangaIngestWithUpscaling uses a pure C# machine learning inference engine powered by **Microsoft.ML.OnnxRuntime**. There is no Python environment, no PyTorch virtual environment, and no runtime pip wheel downloads required.

Hardware acceleration is achieved through native ONNX **Execution Providers (EP)** packaged inside two dedicated Docker images:

---

## Docker Image Flavors

| Image Tag | Execution Provider | Target Hardware | Requirements | Pre-configured `PreferredGpuBackend` |
|---|---|---|---|---|
| `:latest` / `:latest-dev` | **WebGPU** (Vulkan via Mesa RADV / ANV) + CPU fallback | AMD Radeon (RX 5000/6000/7000/9000), Intel Arc / Iris Xe, CPU fallback | Host GPU device access (`/dev/dri`) | `WebGPU` *(built-in default)* |
| `:latest-cuda` / `:latest-dev-cuda` | **CUDA** & **TensorRT** | NVIDIA GeForce GTX/RTX, Quadro, Tesla | NVIDIA driver + `nvidia-container-toolkit` | `CUDA` *(built-in default)* |

Both flavor tags are published for both the main web application (`manga-ingest-with-upscaling`) and the standalone remote worker (`manga-ingest-with-upscaling-remote-worker`). Each container image already has `Ingest_Upscaler__PreferredGpuBackend` pre-set to its matching provider, so you do not need to configure it in Docker Compose unless overriding it.

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

The standard `:latest` image accelerates inference via WebGPU / Vulkan on AMD Radeon and Intel GPUs, with clean CPU fallback if no GPU is available. `PreferredGpuBackend` defaults to `WebGPU` inside this container:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin
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

Use the `:latest-cuda` (or `:latest-dev-cuda`) image for native NVIDIA CUDA & TensorRT acceleration. `PreferredGpuBackend` defaults to `CUDA` inside this container:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-cuda
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin
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

### Black pages

**Symptom:** a page that is coloured in the source comes out of the upscaler entirely black.

**Cause:** every execution provider can run the conv models (all `*_ESRGAN_*`, `*_SPAN_*`), but the WebGPU EP has no kernels for the transformer architectures. Instead of reporting a missing kernel it returns a tensor of NaN for the whole page, and the fp16 output table turns each NaN into a `0` byte — a black page. Affected models: `4x_IllustrationJaNai_*FDAT_M*`, `*FDAT_XL*`, `*DAT2*` and `*HAT_L*`. The same files run correctly on CPU, on CUDA and on DirectML, so the symptom is backend-specific, not a corrupt download.

A page reaches one of those models when `IsGrayscale` classifies it as colour. A manga page with a single coloured panel, or a cover, picks the `IllustrationJaNai_*` family; a fully grayscale page picks `MangaJaNai_*` and is unaffected.

**What the app does:** before a page is upscaled, each candidate model in the preference order is given a 256×256 tile through the real tiling path. A model whose output is not finite is recorded as unusable for the device and the next candidate is tried, so a colour page is upscaled by `…_V1_ESRGAN_135k.onnx` on a device that cannot run the transformer models. Each tile of the page is still checked, because a few models only fail above a certain tile size. If no candidate works, the page fails loudly instead of being written black.

**What you can do:**
- Nothing, if the warning is acceptable — colour pages are still upscaled, just by an older model.
- Set `Upscaler:PreferredGpuBackend` to a provider that has the kernels (`CPU` for correctness regardless of speed, or `CUDA` on an NVIDIA card).
- Run the upscaling on the [remote worker](./REMOTE_WORKER.md) on a machine whose accelerator can run those models, and set `Upscaler:RemoteOnly`.

The warning that names the rejected model looks like this:

```
Model 4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx produced non-finite output on device 0 with the WebGPU execution provider, so it would have written a black page. Trying the next model.
```

