# Upscaling Timeout Configuration

> For a complete overview of all upscaler settings in one place, see
> [Upscaler Configuration](UPSCALER_CONFIGURATION.md).

## Overview

The upscaling timeout controls how long the upscaler waits without receiving any output from the
underlying Python process before considering the run stuck and aborting it.

The timeout is **scaled by the largest image in the archive**: the pixel count of the biggest
image determines the inactivity budget for the entire upscaling run, ensuring that large images
are given adequate time without requiring a fixed, overly conservative timeout.

### Scaling formula

```
effectiveTimeout = UpscaleTimeout × max(1.0, largestImagePixels / 1_000_000)
```

Examples with the default `UpscaleTimeout` of 1 minute:

| Largest image in archive | Effective timeout |
|--------------------------|-------------------|
| 500 × 800 (0.4 MP) | 1 minute (never reduced below the base) |
| 1000 × 1000 (1 MP) | 1 minute |
| 1920 × 1080 (≈2 MP) | ≈2 minutes |
| 3840 × 2160 (≈8 MP) | ≈8 minutes |

When `MaxDimensionBeforeUpscaling` is configured, the pixel count is measured **after** the
downscale, so the timeout correctly reflects the actual work the upscaler has to do.

## Configuration

Set `UpscaleTimeout` in the `Upscaler` section of `appsettings.json`. The value uses the standard
.NET `TimeSpan` string format (`hh:mm:ss`).

```json
{
  "Upscaler": {
    "UpscaleTimeout": "00:01:00"
  }
}
```

### Default

`00:01:00` (1 minute per million pixels of the largest image)

### Choosing a value

- **Slow GPU / CPU-only**: increase to `"00:02:00"` or higher so that a 1 MP image gets 2 minutes.
- **Fast GPU**: the default is usually sufficient; lower it (e.g., `"00:00:30"`) to fail faster on
  genuinely stuck processes.
- **Very large images**: either increase the base timeout or set `MaxDimensionBeforeUpscaling` to
  cap the image size (and thus the effective timeout).

## Combined with MaxDimensionBeforeUpscaling

When both settings are active the image is first downscaled and then the scaled timeout is
computed from the downscaled dimensions:

```json
{
  "Upscaler": {
    "MaxDimensionBeforeUpscaling": 2048,
    "UpscaleTimeout": "00:01:30"
  }
}
```

A 4K image (3840 × 2160 ≈ 8.3 MP) would be downscaled to fit within 2048 px (≈ 4.2 MP) before
upscaling, so the effective timeout would be approximately 6.3 minutes rather than 12.5 minutes.

## Remote Worker

The `UpscaleTimeout` setting is equally available in the Remote Worker's `appsettings.json`:

```json
{
  "Upscaler": {
    "UpscaleTimeout": "00:01:00"
  }
}
```

## See Also

- [IMAGE_FORMAT_CONVERSION.md](IMAGE_FORMAT_CONVERSION.md) – preprocessing images before upscaling
- [REMOTE_WORKER.md](REMOTE_WORKER.md) – setting up distributed upscaling
- [GPU_BACKEND_CONFIGURATION.md](GPU_BACKEND_CONFIGURATION.md) – GPU configuration options
