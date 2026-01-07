using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using NetVips;

namespace MangaIngestWithUpscaling.Services.ImageFiltering;

/// <summary>
/// Perceptual hash implementation using NetVips for image processing.
/// This implementation is designed to be a drop-in replacement for the one in
/// CoenM.ImageHash, maintaining full compatibility with existing hashes.
/// See: https://github.com/coenm/ImageHash
/// </summary>
public class NetVipsPerceptualHash
{
    private const int SIZE = 64;
    private const int MATRIX_SIZE = SIZE * SIZE;
    private static readonly double _sqrt2DivSize = Math.Sqrt(2D / SIZE);
    private static readonly double _sqrt2 = 1 / Math.Sqrt(2);
    private static readonly List<Vector<double>>[] _dctCoeffsSimd = GenerateDctCoeffsSimd();

    /// <summary>
    /// Calculates the 64-bit perceptual hash of an image.
    /// </summary>
    /// <param name="imageBytes">Raw image bytes.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <returns>A 64-bit perceptual hash.</returns>
    public ulong Hash(byte[] imageBytes, ILogger logger)
    {
        if (imageBytes == null)
        {
            throw new ArgumentNullException(nameof(imageBytes));
        }

        using var image = Image.NewFromBuffer(imageBytes);

        // Force a 64x64 resize and auto-orient to match the reference algorithm.
        using Image? resized = image
            .Resize((double)SIZE / image.Width, vscale: (double)SIZE / image.Height)
            .Autorot();

        using Image? processed = resized.HasAlpha() ? resized.Flatten() : resized;
        using Image? grayscale = processed.Colourspace(Enums.Interpretation.Bw);

        byte[] pixelBytes = grayscale.WriteToMemory<byte>();

        // Sanity check the pixel buffer size.
        if (pixelBytes.Length < SIZE * SIZE)
        {
            // The forceful resize should prevent this, but as a safeguard,
            // we handle cases where VIPS might fail to produce a 64x64 image.
            logger.LogError(
                "Warning: Image processing did not result in a 64x64 pixel map. Skipping hash."
            );
            return 0;
        }

        // Rent arrays from pool to reduce GC pressure
        double[] rowsRented = ArrayPool<double>.Shared.Rent(MATRIX_SIZE);
        double[] matrixRented = ArrayPool<double>.Shared.Rent(MATRIX_SIZE);

        try
        {
            Span<double> sequence = stackalloc double[SIZE];

            // Calculate the DCT for each row.
            for (int y = 0; y < SIZE; y++)
            {
                int rowStartIndex = y * SIZE;
                for (int x = 0; x < SIZE; x++)
                {
                    sequence[x] = pixelBytes[rowStartIndex + x];
                }

                Dct1D_SIMD(sequence, rowsRented, y);
            }

            // Calculate the DCT for each column of the low-frequency top-left quadrant.
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < SIZE; y++)
                {
                    sequence[y] = rowsRented[(y * SIZE) + x];
                }

                Dct1D_SIMD(sequence, matrixRented, x, 8);
            }

            // Extract the top 8x8 DCT coefficients.
            Span<double> top8X8 = stackalloc double[SIZE];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    top8X8[(y * 8) + x] = matrixRented[(y * SIZE) + x];
                }
            }

            // Find the median of the coefficients using stack allocation
            Span<double> medianArray = stackalloc double[SIZE];
            double median = CalculateMedian64Values(top8X8, medianArray);

            // Build the hash by comparing each coefficient to the median.
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
        finally
        {
            // Always return arrays to pool
            ArrayPool<double>.Shared.Return(rowsRented);
            ArrayPool<double>.Shared.Return(matrixRented);
        }
    }

    /// <summary>
    /// Calculates the similarity between two perceptual hashes as a percentage.
    /// </summary>
    /// <returns>A value between 0.0 (completely different) and 100.0 (identical).</returns>
    public static double CalculateSimilarity(ulong hash1, ulong hash2)
    {
        int hammingDistance = CalculateHammingDistance(hash1, hash2);
        return (64.0 - hammingDistance) / 64.0 * 100.0;
    }

    /// <summary>
    /// Calculates the Hamming distance between two perceptual hashes.
    /// </summary>
    /// <returns>The number of bits that are different (0-64).</returns>
    public static int CalculateHammingDistance(ulong hash1, ulong hash2)
    {
        return BitOperations.PopCount(hash1 ^ hash2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CalculateMedian64Values(
        ReadOnlySpan<double> values,
        Span<double> tempArray
    )
    {
        // Use the provided temp span instead of allocating new array
        values.CopyTo(tempArray);
        tempArray.Slice(0, values.Length).Sort();

        // Calculate median of 64 values (indices 31 and 32 when 0-indexed)
        return (tempArray[31] + tempArray[32]) / 2.0;
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
            Debug.Assert(SIZE % stride == 0, "Size must be a multiple of SIMD vector width.");
            for (int i = 0; i < SIZE; i += stride)
            {
                singleResultList.Add(new Vector<double>(singleResultRaw, i));
            }

            results[coef] = singleResultList;
        }

        return results;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct1D_SIMD(
        ReadOnlySpan<double> valuesRaw,
        double[] coefficients,
        int ci,
        int limit = SIZE
    )
    {
        int stride = Vector<double>.Count;
        var vectors = new Vector<double>[valuesRaw.Length / stride];
        for (int i = 0; i < valuesRaw.Length; i += stride)
        {
            vectors[i / stride] = new Vector<double>(valuesRaw.Slice(i, stride));
        }

        for (int coef = 0; coef < limit; coef++)
        {
            double sum = 0;
            List<Vector<double>> dctCoeffs = _dctCoeffsSimd[coef];
            for (int i = 0; i < vectors.Length; i++)
            {
                sum += Vector.Dot(vectors[i], dctCoeffs[i]);
            }

            int index = (ci * SIZE) + coef;
            coefficients[index] = sum * _sqrt2DivSize;
            if (coef == 0)
            {
                coefficients[index] *= _sqrt2;
            }
        }
    }
}
