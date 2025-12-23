using MangaIngestWithUpscaling.Shared.Constants;

namespace MangaIngestWithUpscaling.Shared.Tests.Constants;

public class ImageConstantsTests
{
    [Fact]
    public void DetectImageFormatFromHeader_DetectsJpeg()
    {
        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        var result = ImageConstants.DetectImageFormatFromHeader(jpegHeader);
        Assert.Equal(".jpg", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_DetectsPng()
    {
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var result = ImageConstants.DetectImageFormatFromHeader(pngHeader);
        Assert.Equal(".png", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_DetectsWebp()
    {
        byte[] webpHeader =
        [
            0x52,
            0x49,
            0x46,
            0x46, // RIFF
            0x00,
            0x00,
            0x00,
            0x00, // file size (placeholder)
            0x57,
            0x45,
            0x42,
            0x50, // WEBP
        ];
        var result = ImageConstants.DetectImageFormatFromHeader(webpHeader);
        Assert.Equal(".webp", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_DetectsBmp()
    {
        byte[] bmpHeader = [0x42, 0x4D, 0x00, 0x00];
        var result = ImageConstants.DetectImageFormatFromHeader(bmpHeader);
        Assert.Equal(".bmp", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_DetectsTiffLittleEndian()
    {
        byte[] tiffHeader = [0x49, 0x49, 0x2A, 0x00];
        var result = ImageConstants.DetectImageFormatFromHeader(tiffHeader);
        Assert.Equal(".tiff", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_DetectsTiffBigEndian()
    {
        byte[] tiffHeader = [0x4D, 0x4D, 0x00, 0x2A];
        var result = ImageConstants.DetectImageFormatFromHeader(tiffHeader);
        Assert.Equal(".tiff", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_DetectsAvif()
    {
        byte[] avifHeader =
        [
            0x00,
            0x00,
            0x00,
            0x20, // size
            0x66,
            0x74,
            0x79,
            0x70, // ftyp
            0x61,
            0x76,
            0x69,
            0x66, // avif
        ];
        var result = ImageConstants.DetectImageFormatFromHeader(avifHeader);
        Assert.Equal(".avif", result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_ReturnsNullForUnknownFormat()
    {
        byte[] unknownHeader = [0x00, 0x00, 0x00, 0x00];
        var result = ImageConstants.DetectImageFormatFromHeader(unknownHeader);
        Assert.Null(result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_ReturnsNullForInsufficientBytes()
    {
        byte[] tooShort = [0xFF];
        var result = ImageConstants.DetectImageFormatFromHeader(tooShort);
        Assert.Null(result);
    }

    [Fact]
    public void DetectImageFormatFromHeader_ReturnsNullForEmptyArray()
    {
        byte[] empty = [];
        var result = ImageConstants.DetectImageFormatFromHeader(empty);
        Assert.Null(result);
    }

    [Fact]
    public void DetectImageFormatFromFile_ReturnsNullForNonExistentFile()
    {
        var result = ImageConstants.DetectImageFormatFromFile("/nonexistent/file.jpg");
        Assert.Null(result);
    }

    [Fact]
    public void DetectImageFormatFromFile_DetectsJpegFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
            File.WriteAllBytes(tempFile, jpegHeader);

            var result = ImageConstants.DetectImageFormatFromFile(tempFile);
            Assert.Equal(".jpg", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void DetectImageFormatFromFile_DetectsPngFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            File.WriteAllBytes(tempFile, pngHeader);

            var result = ImageConstants.DetectImageFormatFromFile(tempFile);
            Assert.Equal(".png", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void DetectImageFormatFromFile_DetectsWebpFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            byte[] webpHeader =
            [
                0x52,
                0x49,
                0x46,
                0x46,
                0x00,
                0x00,
                0x00,
                0x00,
                0x57,
                0x45,
                0x42,
                0x50,
            ];
            File.WriteAllBytes(tempFile, webpHeader);

            var result = ImageConstants.DetectImageFormatFromFile(tempFile);
            Assert.Equal(".webp", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
