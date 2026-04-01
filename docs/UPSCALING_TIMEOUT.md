# Upscaling Timeout Configuration

> For a complete overview of all upscaler settings in one place, see
> [Upscaler Configuration](UPSCALER_CONFIGURATION.md).

## Overview

The upscaling timeout controls how long the upscaler waits without receiving any output from the
underlying Python process before considering the run stuck and aborting it.

The timeout is **per-image, per-million pixels**: each image in the archive gets its own
inactivity budget proportional to its own pixel count. This ensures that a small image in an
archive also containing a very large image is not given an unreasonably long timeout.

> **Note:** When progress reporting is disabled or unavailable (non-streaming execution), the
> upscaler falls back to a single inactivity timeout derived from the largest image in the
> archive. In that mode, smaller images can effectively share this larger timeout budget.

### Scaling formula

```
effectiveTimeout = UpscaleTimeout × max(1.0, thisImagePixels / 1_000_000)
```

Examples with the default `UpscaleTimeout` of 1 minute:

| Image size | Effective timeout |
|--------------------|-------------------|
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

`00:01:00` (1 minute per million pixels)

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
