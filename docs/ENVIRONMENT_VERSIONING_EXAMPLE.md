# Example: Environment Versioning in Action

This example shows how the environment versioning system works when dependencies change.

## Scenario: Updating PyTorch from 2.7.0 to 2.8.0

### Before the Update

**Current state file (environment_state.json):**
```json
{
  "InstalledBackend": "CUDA",
  "CreatedAt": "2025-01-15T10:30:00Z",
  "PythonVersion": "Python 3.12.0",
  "InstalledPackages": [
    "torch==2.7.0",
    "torchvision==0.22.0",
    "numpy==2.2.5",
    "..."
  ],
  "EnvironmentVersion": 1
}
```

**Source code:**
```csharp
private const int ENVIRONMENT_VERSION = 1;

// In InstallPythonPackages method:
GpuBackend.CUDA => "install torch==2.7.0 torchvision==0.22.0 --index-url https://download.pytorch.org/whl/cu118"
```

### Making the Update

**Step 1: Update dependencies**
```csharp
// Update the torch installation command
GpuBackend.CUDA => "install torch==2.8.0 torchvision==0.23.0 --index-url https://download.pytorch.org/whl/cu118"
```

**Step 2: Increment version and document**
```csharp
/// <summary>
/// Environment version - increment this when Python dependencies change to force environment recreation.
/// 
/// Version History:
/// v1: Initial implementation with torch==2.7.0, torchvision==0.22.0, and base packages
/// v2: Updated torch to 2.8.0, torchvision to 0.23.0 for improved performance and bug fixes
/// 
/// When updating dependencies:
/// 1. Update the package versions in InstallPythonPackages method
/// 2. Increment this ENVIRONMENT_VERSION constant
/// 3. Add a comment above describing the changes
/// </summary>
private const int ENVIRONMENT_VERSION = 2; // Changed from 1 to 2
```

### What Happens on Next Startup

**Application logs:**
```
[INFO] Auto-detecting GPU backend using OpenGL...
[INFO] GPU detection completed, selected backend: CUDA
[INFO] Environment version changed from 1 to 2, recreating environment
[INFO] Creating/recreating Python environment with CUDA backend
[INFO] Installing PyTorch with CUDA backend
[INFO] Saved environment state with CUDA backend, version 2
```

**New state file (environment_state.json):**
```json
{
  "InstalledBackend": "CUDA",
  "CreatedAt": "2025-01-16T09:15:00Z",
  "PythonVersion": "Python 3.12.0",
  "InstalledPackages": [
    "torch==2.8.0",
    "torchvision==0.23.0",
    "numpy==2.2.5",
    "..."
  ],
  "EnvironmentVersion": 2
}
```

## Benefits

1. **Automatic Updates**: Users get the latest dependencies without manual intervention
2. **Consistency**: All environments are guaranteed to have the same dependency versions
3. **Clean State**: Old incompatible packages are completely removed
4. **Auditable**: Version history provides clear tracking of what changed when
5. **Reliable**: No partial updates or dependency conflicts

## Real-World Use Cases

### Security Updates
```csharp
// v2 -> v3: Updated numpy to fix CVE-2024-xxxx
private const int ENVIRONMENT_VERSION = 3;
```

### Performance Improvements
```csharp
// v3 -> v4: Updated to PyTorch 2.9.0 for 15% faster inference
private const int ENVIRONMENT_VERSION = 4;
```

### New Features
```csharp
// v4 -> v5: Added transformers library for new AI models
private const int ENVIRONMENT_VERSION = 5;
```

### Platform Updates
```csharp
// v5 -> v6: Updated CUDA support from 11.8 to 12.1
private const int ENVIRONMENT_VERSION = 6;
```
