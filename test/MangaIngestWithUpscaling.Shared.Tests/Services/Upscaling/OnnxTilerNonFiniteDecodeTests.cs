using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// The end of the non-finite guard: the two <c>throw new NonFiniteModelOutputException()</c> blocks
/// in <see cref="OnnxTiler.DecodeOutputTensor"/>. Deleting either of them leaves the
/// <c>ContainsNonFinite</c> tests and everything else in the suite green, and turns a colour page
/// black again — so they are pinned here through a real session over a generated model that really
/// does return NaN.
/// </summary>
public class OnnxTilerNonFiniteDecodeTests
{
    private const int TileW = 2;
    private const int TileH = 2;
    private const int Scale = 4;
    private const int PaddedW = 2;
    private const int PaddedH = 2;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecodeOutputTensor_Fp16_RejectsNonFiniteOutput(bool nan)
    {
        (byte[] _, byte[]? decoded, Exception? thrown) = Run(nan, fp16: true);

        Assert.IsType<NonFiniteModelOutputException>(thrown);
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecodeOutputTensor_Fp32_RejectsNonFiniteOutput(bool nan)
    {
        (byte[] _, byte[]? decoded, Exception? thrown) = Run(nan, fp16: false);

        Assert.IsType<NonFiniteModelOutputException>(thrown);
        Assert.Null(decoded);
    }

    [Fact]
    public void DecodeOutputTensor_FiniteOutput_StillDecodes()
    {
        // The guard must not reject a model that merely overshoots at a high-contrast edge — that
        // is legitimate output, and the closest neighbours of a NaN are exactly those values.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(CreateDir(), scale: Scale);
        using var session = new InferenceSession(modelPath);

        var input = new DenseTensor<Float16>(
            Enumerable.Repeat(Float16.One, 3 * PaddedW * PaddedH).ToArray(),
            [1, 3, PaddedH, PaddedW]
        );
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([
            NamedOnnxValue.CreateFromTensor(TinyOnnxModel.InputName, input),
        ]);

        byte[] decoded = OnnxTiler.DecodeOutputTensor(
            outputs,
            TileW,
            TileH,
            PaddedW,
            PaddedH,
            Scale,
            CancellationToken.None
        );

        Assert.Equal(TileW * Scale * TileH * Scale * 3, decoded.Length);
    }

    /// <summary>
    /// Runs one generated model through the real tiler decode path. The exception is returned rather
    /// than thrown so the theory can assert on it and on the bytes in one call.
    /// </summary>
    private static (byte[] Input, byte[]? Decoded, Exception? Thrown) Run(bool nan, bool fp16)
    {
        string modelPath = TinyOnnxModel.WriteNonFiniteOutput(CreateDir(), nan, fp16);
        using var session = new InferenceSession(modelPath);
        // The input has to match the model's element type or the session rejects it, which is the
        // same reason the fp16 and fp32 branches in DecodeOutputTensor exist at all.
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = fp16
            ? session.Run([
                NamedOnnxValue.CreateFromTensor(
                    TinyOnnxModel.InputName,
                    new DenseTensor<Float16>(
                        Enumerable.Repeat(Float16.One, 3 * PaddedW * PaddedH).ToArray(),
                        [1, 3, PaddedH, PaddedW]
                    )
                ),
            ])
            : session.Run([
                NamedOnnxValue.CreateFromTensor(
                    TinyOnnxModel.InputName,
                    new DenseTensor<float>(
                        Enumerable.Repeat(1f, 3 * PaddedW * PaddedH).ToArray(),
                        [1, 3, PaddedH, PaddedW]
                    )
                ),
            ]);

        try
        {
            byte[] decoded = OnnxTiler.DecodeOutputTensor(
                outputs,
                TileW,
                TileH,
                PaddedW,
                PaddedH,
                Scale,
                CancellationToken.None
            );
            return ([], decoded, null);
        }
        catch (Exception ex)
        {
            return ([], null, ex);
        }
    }

    private static string CreateDir() =>
        Path.Combine(Path.GetTempPath(), $"nonfinite_decode_{Guid.NewGuid():N}");
}
