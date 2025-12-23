# Python Environment Versioning Guide

The Python environment management system includes a versioning mechanism that automatically recreates environments when dependencies change.

## How It Works

The system tracks an `ENVIRONMENT_VERSION` constant in the `PythonService` class. When this version number changes, all existing Python environments will be automatically recreated on the next startup to ensure they have the latest dependencies.

## When to Increment the Version

Increment the `ENVIRONMENT_VERSION` constant when making any of these changes:

### 1. **Updating PyTorch versions**
```csharp
// OLD
GpuBackend.CUDA => "install torch==2.7.0 torchvision==0.22.0 --index-url https://download.pytorch.org/whl/cu118"

// NEW - increment ENVIRONMENT_VERSION from 1 to 2
GpuBackend.CUDA => "install torch==2.9.1 torchvision==0.24.1 --index-url https://download.pytorch.org/whl/cu118"
```

### 2. **Adding new package dependencies**
```csharp
// OLD
var basePackages = "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86";

// NEW - increment ENVIRONMENT_VERSION from 1 to 2
var basePackages = "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 pillow==10.1.0";
```

### 3. **Updating existing package versions**
```csharp
// OLD
"chainner_ext==0.3.10 numpy==2.2.5"

// NEW - increment ENVIRONMENT_VERSION from 1 to 2
"chainner_ext==0.3.11 numpy==2.3.0"
```

### 4. **Changing PyTorch index URLs**
```csharp
// OLD
GpuBackend.CUDA => "--index-url https://download.pytorch.org/whl/cu118"

// NEW - increment ENVIRONMENT_VERSION from 1 to 2
GpuBackend.CUDA => "--index-url https://download.pytorch.org/whl/cu121"
```

## How to Update

### Step 1: Make your dependency changes
Update the package versions, add new packages, or modify PyTorch configurations in the `InstallPythonPackages` method.

### Step 2: Increment the version
```csharp
/// <summary>
/// Environment version - increment this when Python dependencies change to force environment recreation.
/// 
/// Version History:
/// v1: Initial implementation with torch==2.7.0, torchvision==0.22.0, and base packages
/// v2: Updated torch to 2.8.0, torchvision to 0.23.0, added pillow==10.1.0
/// 
/// When updating dependencies:
/// 1. Update the package versions in InstallPythonPackages method
/// 2. Increment this ENVIRONMENT_VERSION constant
/// 3. Add a comment above describing the changes
/// </summary>
private const int ENVIRONMENT_VERSION = 2; // Changed from 1 to 2
```

### Step 3: Document the changes
Add a line to the version history comment describing what changed in this version.

## What Happens Next

- On the next application startup, the system will detect that `ENVIRONMENT_VERSION = 2` but existing environments have `EnvironmentVersion = 1`
- All existing Python environments will be automatically deleted and recreated with the new dependencies
- Users will see log messages like: `"Environment version changed from 1 to 2, recreating environment"`
- The process is automatic and requires no user intervention

## Example Log Output

```
[INFO] Auto-detecting GPU backend using OpenGL...
[INFO] GPU detection completed, selected backend: CUDA
[INFO] Environment version changed from 1 to 2, recreating environment
[INFO] Creating/recreating Python environment with CUDA backend
[INFO] Installing PyTorch with CUDA backend
[INFO] Saved environment state with CUDA backend, version 2
```

## Best Practices

1. **Always increment for dependency changes** - Even small version bumps should trigger recreation
2. **Document version changes** - Keep the version history comment up to date
3. **Test thoroughly** - Verify that new dependencies work correctly
4. **Coordinate releases** - Version increments should be part of coordinated releases
5. **Consider impact** - Environment recreation can take several minutes for users

## Troubleshooting

If users report issues after a version update:

1. **Check logs** for environment recreation messages
2. **Verify** the environment was actually recreated (check timestamps)
3. **Manually delete** environment directories if automatic recreation fails
4. **Check internet connectivity** for package downloads
