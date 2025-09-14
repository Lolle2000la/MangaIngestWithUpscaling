using NetVips;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

/// <summary>
///     Perceptual hash implementation using NetVips instead of ImageSharp.
///     This implementation maintains compatibility with existing hashes by following the same algorithm
///     as CoenM.ImageHash.HashAlgorithms.PerceptualHash
///     (see https://github.com/coenm/ImageHash/blob/develop/src/ImageHash/HashAlgorithms/PerceptualHash.cs).
///     Calculate a hash of an image by first transforming the image to a 64x64 grayscale bitmap
///     and then using the Discrete cosine transform to remove the high frequencies.
///     This allows us to continue to use the same hashes as before.
/// </summary>
public class NetVipsPerceptualHash
{
    private const int SIZE = 64;
    private static readonly double _sqrt2DivSize = Math.Sqrt(2D / SIZE);
    private static readonly double _sqrt2 = 1 / Math.Sqrt(2);
    private static readonly List<Vector<double>>[] _dctCoeffsSimd = GenerateDctCoeffsSimd();

    /// <summary>
    ///     Calculates the perceptual hash of an image using NetVips
    /// </summary>
    /// <param name="imageBytes">Raw image bytes</param>
    /// <returns>64-bit perceptual hash</returns>
    public ulong Hash(byte[] imageBytes)
    {
        if (imageBytes == null)
        {
            throw new ArgumentNullException(nameof(imageBytes));
        }

        // Load image with NetVips
        using var image = Image.NewFromBuffer(imageBytes);

        double[,] rows = new double[SIZE, SIZE];
        double[] sequence = new double[SIZE];
        double[,] matrix = new double[SIZE, SIZE];

        // Resize to exactly 64x64, convert to grayscale using BT.601, and auto-orient
        // This matches ImageSharp's behavior exactly: forced resize (may stretch) + BT.601 grayscale
        using Image? resized = image
            .Resize((double)SIZE / image.Width,
                vscale: (double)SIZE / image.Height) // Force exact 64x64 (may stretch like ImageSharp)
            .Autorot(); // Auto-orient

        // Convert to grayscale using BT.601 weights (0.299*R + 0.587*G + 0.114*B)
        // This matches ImageSharp's GrayscaleMode.Bt601 exactly
        using Image? processed = resized.HasAlpha()
            ? resized.Flatten() // Remove alpha channel if present
            : resized;

        using Image? grayscale = processed.Bands >= 3
            ? processed.Colourspace(Enums.Interpretation.Srgb)
                .Colourspace(Enums.Interpretation.Bw) // Proper BT.601 conversion
            : processed.Colourspace(Enums.Interpretation.Bw); // Already grayscale

        // Extract pixel data as grayscale values
        double[,] pixelData = ExtractGrayscalePixels(grayscale);

        // Calculate the DCT for each row.
        for (int y = 0; y < SIZE; y++)
        {
            for (int x = 0; x < SIZE; x++)
            {
                sequence[x] = pixelData[x, y]; // Match ImageSharp's access pattern: image[x, y].R
            }

            Dct1D_SIMD(sequence, rows, y);
        }

        // Calculate the DCT for each column.
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < SIZE; y++)
            {
                sequence[y] = rows[y, x];
            }

            Dct1D_SIMD(sequence, matrix, x, 8);
        }

        // Only use the top 8x8 values.
        double[] top8X8 = new double[SIZE];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                top8X8[(y * 8) + x] = matrix[y, x];
            }
        }

        // Get Median.
        double median = CalculateMedian64Values(top8X8);

        // Calculate hash.
        ulong mask = 1UL << (SIZE - 1);
        ulong hash = 0UL;

        for (int i = 0; i < SIZE; i++)
        {
            if (top8X8[i] > median)
            {
                hash |= mask;
            }

            mask >>= 1;
        }

        return hash;
    }

    /// <summary>
    ///     Calculates the similarity between two perceptual hashes.
    ///     Returns a value between 0.0 and 100.0 where 100.0 means identical images.
    ///     This is compatible with CoenM.ImageHash.CompareHash.Similarity
    /// </summary>
    /// <param name="hash1">First perceptual hash</param>
    /// <param name="hash2">Second perceptual hash</param>
    /// <returns>Similarity percentage (0-100)</returns>
    public static double CalculateSimilarity(ulong hash1, ulong hash2)
    {
        // Calculate Hamming distance by counting different bits
        ulong xor = hash1 ^ hash2;
        int hammingDistance = CountSetBits(xor);

        // Convert to similarity percentage
        // 64-bit hash: 0 distance = 100% similarity, 64 distance = 0% similarity
        double similarity = (64.0 - hammingDistance) / 64.0 * 100.0;
        return Math.Max(0.0, Math.Min(100.0, similarity));
    }

    /// <summary>
    ///     Calculates the Hamming distance between two perceptual hashes
    /// </summary>
    /// <param name="hash1">First perceptual hash</param>
    /// <param name="hash2">Second perceptual hash</param>
    /// <returns>Hamming distance (0-64)</returns>
    public static int CalculateHammingDistance(ulong hash1, ulong hash2)
    {
        ulong xor = hash1 ^ hash2;
        return CountSetBits(xor);
    }

    /// <summary>
    ///     Counts the number of set bits in a ulong value
    /// </summary>
    /// <param name="value">Value to count bits in</param>
    /// <returns>Number of set bits</returns>
    private static int CountSetBits(ulong value)
    {
        // Use built-in bit counting for performance
        return BitOperations.PopCount(value);
    }

    /// <summary>
    ///     Extracts grayscale pixel values from a NetVips image
    /// </summary>
    /// <param name="image">Grayscale NetVips image</param>
    /// <returns>2D array of pixel values (0-255 range) indexed as [x, y] to match ImageSharp</returns>
    private static double[,] ExtractGrayscalePixels(Image image)
    {
        Debug.Assert(image.Width == SIZE && image.Height == SIZE, $"Image must be {SIZE}x{SIZE}");

        double[,] pixels = new double[SIZE, SIZE]; // [x, y] indexing to match ImageSharp

        // Get the raw pixel data
        byte[]? memory = image.WriteToMemory();
        byte[] bytes = new byte[memory.Length];
        memory.CopyTo(bytes.AsSpan());

        // For grayscale images, each pixel is represented by one byte
        // NetVips grayscale images should have 1 band
        Debug.Assert(image.Bands == 1, "Grayscale image should have 1 band");

        // NetVips stores pixels in row-major order: bytes[(y * width) + x]
        // But we need to store in [x, y] format to match ImageSharp's indexer
        for (int y = 0; y < SIZE; y++)
        {
            for (int x = 0; x < SIZE; x++)
            {
                int index = (y * SIZE) + x; // Row-major index in byte array
                pixels[x, y] = bytes[index]; // Store as [x, y] like ImageSharp
            }
        }

        return pixels;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CalculateMedian64Values(IReadOnlyCollection<double> values)
    {
        Debug.Assert(values.Count == 64, "This DCT method works with 64 doubles.");
        return values.OrderBy(value => value).Skip(31).Take(2).Average();
    }

    private static List<Vector<double>>[] GenerateDctCoeffsSimd()
    {
        var results = new List<Vector<double>>[SIZE];
        for (int coef = 0; coef < SIZE; coef++)
        {
            double[] singleResultRaw = new double[SIZE];
            for (int i = 0; i < SIZE; i++)
            {
                singleResultRaw[i] = Math.Cos(((2.0 * i) + 1.0) * coef * Math.PI / (2.0 * SIZE));
            }

            var singleResultList = new List<Vector<double>>();
            int stride = Vector<double>.Count;
            Debug.Assert(SIZE % stride == 0, "Size must be a multiple of SIMD stride");
            for (int i = 0; i < SIZE; i += stride)
            {
                var v = new Vector<double>(singleResultRaw, i);
                singleResultList.Add(v);
            }

            results[coef] = singleResultList;
        }

        return results;
    }

    /// <summary>
    ///     One dimensional Discrete Cosine Transformation.
    /// </summary>
    /// <param name="valuesRaw">Should be an array of doubles of length 64.</param>
    /// <param name="coefficients">Coefficients.</param>
    /// <param name="ci">Coefficients index.</param>
    /// <param name="limit">Limit.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct1D_SIMD(double[] valuesRaw, double[,] coefficients, int ci, int limit = SIZE)
    {
        Debug.Assert(valuesRaw.Length == 64, "This DCT method works with 64 doubles.");

        var valuesList = new List<Vector<double>>();
        int stride = Vector<double>.Count;
        for (int i = 0; i < valuesRaw.Length; i += stride)
        {
            valuesList.Add(new Vector<double>(valuesRaw, i));
        }

        for (int coef = 0; coef < limit; coef++)
        {
            for (int i = 0; i < valuesList.Count; i++)
            {
                coefficients[ci, coef] += Vector.Dot(valuesList[i], _dctCoeffsSimd[coef][i]);
            }

            coefficients[ci, coef] *= _sqrt2DivSize;
            if (coef == 0)
            {
                coefficients[ci, coef] *= _sqrt2;
            }
        }
    }
}