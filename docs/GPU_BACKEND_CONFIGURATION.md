# GPU Backend Configuration Examples

The enhanced Python environment management system now supports automatic detection and manual configuration of GPU backends for PyTorch using OpenGL-based GPU detection.

## Configuration Options

### Automatic Detection (Default)
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "Auto"
  }
}
```

The system will automatically detect available hardware using **Silk.NET.OpenGL**:
- If NVIDIA GPU is detected (via OpenGL vendor/renderer strings), CUDA backend will be used
- If AMD GPU is detected (via OpenGL vendor/renderer strings), ROCm backend will be used  
- If Intel discrete GPU is detected (Arc series), XPU backend will be used
- If Intel integrated GPU is detected, CPU backend will be used (integrated GPUs not optimally supported for ML)
- If no compatible GPU is found, CPU backend will be used

**GPU Detection Method:**
- Creates an offscreen OpenGL context using Silk.NET.Windowing
- Queries GPU vendor and renderer information via OpenGL
- Matches vendor/renderer strings against known patterns:
  - **NVIDIA**: `nvidia`, `geforce`, `quadro`, `tesla`
  - **AMD**: `amd`, `ati`, `radeon` 
  - **Intel discrete GPUs**: `arc`, `xe`, `dg`, `xe-hpg`, `xe-lpg`
  - **Intel integrated GPUs**: `intel` (general Intel GPUs not matching discrete patterns)

### Manual Configuration

#### Force CUDA Backend (11.8)
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CUDA"
  }
}
```

#### Force CUDA 12.8 Backend
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CUDA_12_8"
  }
}
```

#### Force ROCm Backend
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "ROCm"
  }
}
```

#### Force Intel XPU Backend
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "XPU"
  }
}
```

#### Force CPU Backend
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CPU"
  }
}
```

#### Force Accept Existing Environment (Docker/Pre-built)
```json
{
  "Upscaler": {
    "ForceAcceptExistingEnvironment": true
  }
}
```

This option forces the system to accept any existing Python environment without version or backend checks. This is useful for:
- **Docker containers** with pre-built Python environments
- **Pre-configured systems** where dependencies should not be modified
- **Offline environments** where package downloads are not possible

Note that even if the environment is broken, no a attempt at fixing it will be made. The system will continue to use the existing environment as-is. Use with caution.

## Environment Tracking

The system now tracks the installed backend in each Python virtual environment using a `environment_state.json` file. This includes:

- **InstalledBackend**: The GPU backend that was installed (CUDA, ROCm, XPU, or CPU)
- **CreatedAt**: When the environment was created
- **PythonVersion**: Version of Python used
- **InstalledPackages**: List of installed packages
- **EnvironmentVersion**: Version of the environment configuration

If the desired backend changes or the environment version is updated (indicating dependency changes), the system will automatically recreate the environment with the correct PyTorch installation.

### Automatic Environment Recreation

The environment will be automatically recreated when:
1. **Backend change**: The preferred GPU backend has changed
2. **Version update**: The environment version has been incremented (indicating dependency updates)
3. **Missing files**: Python executable or state file is missing
4. **State corruption**: Environment state file is corrupted or unreadable

## PyTorch Installation Details

### CUDA Backend (11.8)
- Installs: `torch==2.8.0 torchvision==0.23.0` from CUDA 11.8 index
- Compatible with NVIDIA GPUs

### CUDA 12.8 Backend
- Installs: `torch==2.8.0 torchvision==0.23.0` from CUDA 12.8 index  
- Compatible with NVIDIA GPUs (requires CUDA 12.8+ drivers)
- Must be manually configured (not auto-detected)

### ROCm Backend  
- Installs: `torch==2.8.0 torchvision==0.23.0` from ROCm 6.3 index
- Compatible with AMD GPUs

### Intel XPU Backend
- Installs: `torch==2.8.0 torchvision==0.23.0` from Intel XPU index
- Compatible with Intel Arc discrete GPUs and Intel Xe GPUs

### CPU Backend
- Installs: `torch==2.8.0 torchvision==0.23.0` from CPU-only index
- Compatible with any system

## Environment Variables

You can also set the backend via environment variables:
```bash
export Ingest_Upscaler__PreferredGpuBackend=CUDA
export Ingest_Upscaler__PreferredGpuBackend=CUDA_12_8
export Ingest_Upscaler__PreferredGpuBackend=ROCm
export Ingest_Upscaler__PreferredGpuBackend=XPU
export Ingest_Upscaler__PreferredGpuBackend=CPU
```

## Advantages of OpenGL-based Detection

1. **Cross-platform**: Works on Windows, Linux, and macOS
2. **No external dependencies**: Doesn't require command-line tools like `nvidia-smi` or `rocm-smi`
3. **Reliable**: Uses established OpenGL APIs to query GPU information
4. **Lightweight**: Creates minimal overhead with offscreen context
5. **Comprehensive**: Can detect GPU information even on headless systems

## Troubleshooting

If GPU detection fails, the system will:
1. Log a warning with the error details
2. Fall back to CPU backend automatically
3. Continue operation without interruption

You can always override automatic detection by setting a specific backend in configuration.
