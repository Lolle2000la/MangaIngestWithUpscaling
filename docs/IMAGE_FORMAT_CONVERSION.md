# Image Format Conversion Feature

## Overview

The Image Format Conversion feature allows you to automatically convert specific image formats to other formats during the upscaling preprocessing step. This conversion happens on a temporary working copy of the images, so your original files remain unchanged.

**Default Behavior**: By default, PNG images are automatically converted to JPG with quality 98 before upscaling to ensure compatibility with the upscaler. This behavior can be disabled or customized through configuration.

## Configuration

Image format conversion is configured through the `Upscaler` section in your `appsettings.json` file using the `ImageFormatConversionRules` array.

### Configuration Structure

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": [
      {
        "FromFormat": ".png",
        "ToFormat": ".jpg",
        "Quality": 95
      },
      {
        "FromFormat": ".webp",
        "ToFormat": ".png"
      }
    ]
  }
}
```

### Configuration Properties

#### ImageFormatConversionRule

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `FromFormat` | string | Yes | - | The source image format to convert from (e.g., ".png", ".jpg"). Case-insensitive. |
| `ToFormat` | string | Yes | - | The target image format to convert to (e.g., ".png", ".jpg"). Case-insensitive. |
| `Quality` | int? | No | 95 | Quality setting for lossy formats (1-100). Not used for lossless formats like PNG. |

### Supported Formats

The following image formats are supported for conversion:

- `.jpg` / `.jpeg` - JPEG (lossy, supports quality setting)
- `.png` - PNG (lossless, ignores quality setting)
- `.webp` - WebP (supports quality setting)
- `.avif` - AVIF (supports quality setting)
- `.bmp` - Bitmap
- `.tiff` / `.tif` - TIFF

## Use Cases

### 1. Disable Default PNG to JPEG Conversion

If you want to upscale PNG images without conversion, set `ImageFormatConversionRules` to an empty array:

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": []
  }
}
```

### 2. Customize PNG to JPEG Conversion Quality

The default PNG to JPEG conversion uses quality 98. You can adjust this:

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": [
      {
        "FromFormat": ".png",
        "ToFormat": ".jpg",
        "Quality": 95
      }
    ]
  }
}
```

### 3. Convert WebP to PNG for Compatibility

Some upscaling models work better with PNG format:

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": [
      {
        "FromFormat": ".webp",
        "ToFormat": ".png"
      }
    ]
  }
}
```

### 4. Multiple Conversion Rules

You can apply multiple conversion rules simultaneously:

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": [
      {
        "FromFormat": ".png",
        "ToFormat": ".jpg",
        "Quality": 95
      },
      {
        "FromFormat": ".webp",
        "ToFormat": ".jpg",
        "Quality": 90
      },
      {
        "FromFormat": ".bmp",
        "ToFormat": ".png"
      }
    ]
  }
}
```

### 5. Combined with Image Resizing

Format conversion works seamlessly with the `MaxDimensionBeforeUpscaling` feature:

```json
{
  "Upscaler": {
    "MaxDimensionBeforeUpscaling": 2048,
    "ImageFormatConversionRules": [
      {
        "FromFormat": ".png",
        "ToFormat": ".jpg",
        "Quality": 95
      }
    ]
  }
}
```

In this configuration:
1. Images are first resized if they exceed 2048px in either dimension
2. Then, any PNG images are converted to JPEG
3. Finally, the preprocessed images are sent to the upscaler

## How It Works

### Processing Pipeline

When image format conversion is enabled:

1. **Extraction**: The CBZ archive is extracted to a temporary directory
2. **Preprocessing** (applied in order):
   - If `MaxDimensionBeforeUpscaling` is set: Images are resized
   - If format conversion rules match: Images are converted to the target format
3. **Archive Creation**: A temporary CBZ is created with preprocessed images
4. **Upscaling**: The temporary CBZ is sent to the upscaler
5. **Cleanup**: Temporary files are automatically deleted

### File Naming

When an image is converted:
- The original filename is preserved
- Only the file extension changes
- Example: `page001.png` → `page001.jpg`

### Original Files

**Important**: Format conversion only affects the temporary working copy used during upscaling. Your original CBZ files and their contents remain completely unchanged.

## Quality Settings

### Lossy Formats (JPEG, WebP, AVIF)

For lossy formats, the `Quality` parameter controls the compression level:
- **1-50**: Low quality, smaller file size
- **51-75**: Medium quality
- **76-90**: Good quality (recommended for most uses)
- **91-95**: High quality (good balance between quality and size)
- **96-100**: Very high quality, larger file size

### Lossless Formats (PNG)

For lossless formats like PNG, the `Quality` parameter is ignored since these formats don't support lossy compression.

## Performance Considerations

### When to Use Format Conversion

**Convert to JPEG when:**
- You have large PNG files with photographic content
- Processing speed is more important than perfect quality
- You want to reduce memory usage during upscaling

**Convert to PNG when:**
- You need lossless quality
- Images have sharp edges or text
- The upscaling model works better with lossless input

### Performance Impact

- **Conversion overhead**: Minimal (typically <1 second per image)
- **Storage benefit**: Converting PNG to JPEG can reduce temporary disk usage by 50-80%
- **Processing benefit**: Smaller files can upscale faster, especially on GPUs with limited memory

## Environment Variables

You can also configure format conversion via environment variables:

```bash
# Not directly supported via environment variables
# Use appsettings.json or appsettings.Development.json instead
```

## Logging

The feature logs preprocessing operations at the following levels:

- **Information**: When preprocessing is triggered and summary statistics
- **Debug**: Individual image conversions and their parameters

Example log output:
```
[INF] Creating temporary preprocessed CBZ (max dimension: 2048, conversion rules: 1) for /path/to/manga.cbz
[DBG] Converting image /tmp/.../page001.png from .png to .jpg
[INF] Using preprocessed temporary file for upscaling: /tmp/preprocessed_abc123_manga.cbz
```

## Remote Worker

The format conversion feature is also available in the Remote Worker. Configure it the same way in the Remote Worker's `appsettings.json`:

```json
{
  "Upscaler": {
    "ImageFormatConversionRules": [
      {
        "FromFormat": ".png",
        "ToFormat": ".jpg",
        "Quality": 95
      }
    ]
  }
}
```

## Troubleshooting

### Images Not Being Converted

**Check the format string:**
- Ensure formats include the dot (`.png`, not `png`)
- Format strings are case-insensitive (`.PNG` works the same as `.png`)

**Check logs:**
- Enable debug logging to see which images are being processed
- Look for "Converting image" messages

### Quality Issues

**If converted images look worse:**
- Increase the `Quality` setting (try 95-98)
- Consider using PNG for images with text or sharp edges

**If conversion is too slow:**
- The conversion process is already optimized with parallel processing
- Consider converting only specific formats that cause issues

### Disk Space Issues

- Temporary files are automatically cleaned up after upscaling
- If you run out of space, the system will fail gracefully
- Monitor your temp directory (`/tmp` on Linux, `%TEMP%` on Windows)

## API Integration

If you're using the programmatic API, you can configure format conversion like this:

```csharp
var upscalerConfig = new UpscalerConfig
{
    MaxDimensionBeforeUpscaling = 2048,
    ImageFormatConversionRules = new List<ImageFormatConversionRule>
    {
        new()
        {
            FromFormat = ".png",
            ToFormat = ".jpg",
            Quality = 95
        }
    }
};
```

## See Also

- [REMOTE_ONLY_VARIANT.md](REMOTE_ONLY_VARIANT.md) - Running without local ML dependencies
- [REMOTE_WORKER.md](REMOTE_WORKER.md) - Setting up distributed upscaling
- [GPU_BACKEND_CONFIGURATION.md](GPU_BACKEND_CONFIGURATION.md) - GPU configuration options

