# GPU Backend Configuration Examples

The enhanced Python environment management system now supports automatic detection and manual configuration of GPU backends for PyTorch.

## Configuration Options

### Automatic Detection (Default)
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "Auto"
  }
}
```

The system will automatically detect available hardware:
- If NVIDIA GPU is detected (via `nvidia-smi`), CUDA backend will be used
- If AMD GPU is detected (via `rocm-smi` or `lspci`), ROCm backend will be used  
- If no compatible GPU is found, CPU backend will be used

### Manual Configuration

#### Force CUDA Backend
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CUDA"
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

#### Force CPU Backend
```json
{
  "Upscaler": {
    "PreferredGpuBackend": "CPU"
  }
}
```

## Environment Tracking

The system now tracks the installed backend in each Python virtual environment using a `environment_state.json` file. This includes:

- **InstalledBackend**: The GPU backend that was installed (CUDA, ROCm, or CPU)
- **CreatedAt**: When the environment was created
- **PythonVersion**: Version of Python used
- **InstalledPackages**: List of installed packages

If the desired backend changes, the system will automatically recreate the environment with the correct PyTorch installation.

## PyTorch Installation Details

### CUDA Backend
- Installs: `torch==2.7.0 torchvision==0.22.0` from CUDA 11.8 index
- Compatible with NVIDIA GPUs

### ROCm Backend  
- Installs: `torch==2.7.0 torchvision==0.22.0` from ROCm 6.3 index
- Compatible with AMD GPUs

### CPU Backend
- Installs: `torch==2.7.0 torchvision==0.22.0` from CPU-only index
- Compatible with any system

## Environment Variables

You can also set the backend via environment variables:
```bash
export Ingest_Upscaler__PreferredGpuBackend=CUDA
```

## Testing GPU Detection

Use the included test script to verify GPU detection:
```bash
python3 test_gpu_backend.py
```
