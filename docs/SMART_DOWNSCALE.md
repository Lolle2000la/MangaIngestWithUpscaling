# Smart Downscale

> For a complete overview of all upscaler settings in one place, see
> [Upscaler Configuration](UPSCALER_CONFIGURATION.md).

## Overview

The **Smart Downscale** feature detects manga images that have been cheaply upscaled (e.g. with
bicubic or bilinear interpolation) and reduces them back toward their likely native resolution
**before** AI upscaling. This prevents "double-upscaling" artefacts and ensures the model sees
clean, high-contrast edges instead of the blurry interpolation residue left by a cheap upscale.

Smart Downscale is **disabled by default**. It is most useful when your source library contains
scans that were upscaled before being distributed.

## How It Works

Detection runs in two stages on a 512 × 512 centre crop of each image:

1. **Laplacian sharpness check** (fast) — measures the standard deviation of a Laplacian edge
   filter. A small value indicates a blurry, low-detail image typical of cheap upscales. A mild
   Gaussian pre-blur suppresses screentone halftone dots so they do not inflate the score.

2. **FFT frequency-cliff detection** (precise, only when Stage 1 triggers) — performs a 2-D
   forward FFT on the greyscale crop using [Math.NET Numerics](https://numerics.mathdotnet.com/)
   (pure C#, no native FFTW dependency). When an image has been cheaply upscaled, its power
   spectrum has a sharp "dead zone" above the original Nyquist frequency. The algorithm measures
   where this cliff sits and infers the exact scale factor from it. If no clear cliff is found the
   configured `SmartDownscaleFactor` is used as a fallback.

All processing is performed on a **temporary working copy**; your original CBZ files are never
modified.

## Configuration

### Enabling the feature

```yaml
# docker-compose
environment:
  Ingest_Upscaler__EnableSmartDownscale: "true"
```

```json
// appsettings.json
{
  "Upscaler": {
    "EnableSmartDownscale": true
  }
}
```

### All settings

| Setting | ENV variable | Default | Description |
|---|---|---|---|
| `EnableSmartDownscale` | `Ingest_Upscaler__EnableSmartDownscale` | `false` | Enable or disable the feature. |
| `SmartDownscaleThreshold` | `Ingest_Upscaler__SmartDownscaleThreshold` | `15.0` | Laplacian std-dev below which an image is considered cheaply upscaled. Lower = stricter (fewer images downscaled); higher = more aggressive. |
| `SmartDownscaleFactor` | `Ingest_Upscaler__SmartDownscaleFactor` | `0.75` | Fallback scale factor when the FFT finds no clear cliff. `0.75` reduces the image to 75 % of its current dimensions. |

### Tuning the threshold

The default threshold of **15.0** is a reasonable starting point for typical manga scans. If you
find that genuine native-resolution images are being downscaled (false positives), decrease the
threshold. If cheaply upscaled images are not being caught (false negatives), increase it.

Enable debug logging to see the Laplacian score for each image:

```
[DBG] Smart downscale check for page001.jpg: Laplacian std-dev = 3.18 (threshold 15)
[INF] Image page001.jpg appears cheaply upscaled (sharpness 3.18 < 15);
      FFT cliff detected at 31 % of Nyquist – will downscale by 0.313
```

A high-contrast manga page typically scores **200 – 600**; a heavily blurred or interpolated page
typically scores below **10**.

## Example configurations

### Conservative — only catch obviously blurry images

```yaml
environment:
  Ingest_Upscaler__EnableSmartDownscale: "true"
  Ingest_Upscaler__SmartDownscaleThreshold: "5.0"
```

### Aggressive — catch lightly upscaled images too

```yaml
environment:
  Ingest_Upscaler__EnableSmartDownscale: "true"
  Ingest_Upscaler__SmartDownscaleThreshold: "30.0"
```

### Fixed factor fallback (when FFT cliff is ambiguous)

```yaml
environment:
  Ingest_Upscaler__EnableSmartDownscale: "true"
  Ingest_Upscaler__SmartDownscaleThreshold: "15.0"
  Ingest_Upscaler__SmartDownscaleFactor: "0.5"   # reduce to 50 % if cliff not found
```

### Combined with max-dimension limit

```json
{
  "Upscaler": {
    "EnableSmartDownscale": true,
    "SmartDownscaleThreshold": 15.0,
    "SmartDownscaleFactor": 0.75,
    "MaxDimensionBeforeUpscaling": 2048
  }
}
```

When both are active, the two constraints are resolved into a **single resize pass**.
The effective scale factor is the smaller (more restrictive) of:

- the max-dimension scale factor (e.g. 0.5 to fit a 4096-wide image into 2048 px), and
- the smart-downscale factor inferred by Laplacian + FFT analysis (or `SmartDownscaleFactor` as fallback).

Detection always runs on the original image. The single combined resize is then applied once
to the original, avoiding the quality loss that would come from two sequential resampling steps.

The processing order is:

1. Detect scale factors (Laplacian sharpness + FFT cliff on the original image; max-dimension constraint)
2. Apply the single combined resize (min of both scale factors), if any resize is needed
3. Format conversion (if a matching rule exists)
4. AI upscaling

## Processing Pipeline

```
Original CBZ
     │
     ▼
Extract to temp dir
     │
     ▼ for each image:
 ┌──────────────────────────────────────────────────────┐
 │  Detection (runs on original image)                  │
 │                                                      │
 │  Stage 1: Laplacian sharpness check (fast)           │
 │    score ≥ threshold → image is sharp → skip         │
 │    score < threshold → proceed to Stage 2            │
 ├──────────────────────────────────────────────────────┤
 │  Stage 2: FFT frequency-cliff detection (precise)    │
 │    cliff found   → use inferred factor (e.g. 0.31)   │
 │    no clear cliff → use SmartDownscaleFactor (0.75)  │
 └──────────────────────────────────────────────────────┘
      │
      ▼
 Combine smart-downscale factor and max-dimension factor
 → take the smaller (more restrictive) scale factor
 → apply ONE resize to the original image (or skip if scale ≥ 1)
     │
     ▼
Pack into temp CBZ → AI upscaler → output
```

## Remote Worker

Smart Downscale is also available in the Remote Worker. Configure it identically in the Remote
Worker's `appsettings.json`:

```json
{
  "Upscaler": {
    "EnableSmartDownscale": true,
    "SmartDownscaleThreshold": 15.0,
    "SmartDownscaleFactor": 0.75
  }
}
```

## See Also

- [Upscaler Configuration](UPSCALER_CONFIGURATION.md) — all settings in one place
- [Image Format Conversion](IMAGE_FORMAT_CONVERSION.md) — convert formats before upscaling
- [Remote Worker](REMOTE_WORKER.md) — distributed upscaling setup
