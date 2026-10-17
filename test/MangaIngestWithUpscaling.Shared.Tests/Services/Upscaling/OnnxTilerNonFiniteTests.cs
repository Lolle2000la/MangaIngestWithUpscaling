using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class OnnxTilerNonFiniteOutputTests
{
    // ---- non-finite output guard -------------------------------------------------------------
    // The WebGPU EP returns an all-NaN tensor instead of reporting a missing kernel, and the fp16
    // table used to turn every one of those values into a 0 byte: a black page, silently written.
    // FDAT_M, FDAT_XL, DAT2 and HAT_L all behave this way there, while the identical model runs
    // correctly on CPU, CUDA and DirectML.

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ContainsNonFiniteFp16_DetectsNanAndInfinity(float poison)
    {
        Float16[] values = [(Float16)0.25f, (Float16)0.5f, (Float16)poison, (Float16)0.75f];

        Assert.True(OnnxTiler.ContainsNonFinite(values));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ContainsNonFiniteFp32_DetectsNanAndInfinity(float poison)
    {
        float[] values = [0.25f, 0.5f, poison, 0.75f];

        Assert.True(OnnxTiler.ContainsNonFinite(values));
    }

    [Fact]
    public void ContainsNonFiniteFp16_AcceptsFiniteValuesOutsideUnitRange()
    {
        // Models legitimately overshoot at high-contrast edges, and the smallest representable
        // value is finite. Both clamp to 0 or 255; only NaN and infinity are a failure.
        Float16[] values =
        [
            (Float16)(-0.25f),
            (Float16)1.25f,
            (Float16)0.00000005960464477539f,
            (Float16)0f,
            (Float16)1f,
        ];

        Assert.False(OnnxTiler.ContainsNonFinite(values));
    }

    [Fact]
    public void ContainsNonFiniteFp32_AcceptsFiniteValuesOutsideUnitRange()
    {
        Assert.False(OnnxTiler.ContainsNonFinite([-0.25f, 1.25f, 0f, 1f]));
    }

    [Fact]
    public void ContainsNonFinite_EmptyTensor_ReturnsFalse()
    {
        Assert.False(OnnxTiler.ContainsNonFinite(ReadOnlySpan<Float16>.Empty));
        Assert.False(OnnxTiler.ContainsNonFinite(ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void ContainsNonFinite_LargeFp16Tensor_DetectsTheSinglePoisonedValue()
    {
        // Big enough that the scan leaves the cache and the poisoned value sits near the end,
        // so a short-circuiting or mis-vectorised read would be visible.
        Float16[] values = new Float16[1 << 20];
        Array.Fill(values, (Float16)0.42f);
        values[^3] = (Float16)float.NaN;

        Assert.True(OnnxTiler.ContainsNonFinite(values));
    }

    [Fact]
    public void ContainsNonFinite_DenseTensorBuffer_DetectsTheSinglePoisonedValue()
    {
        var tensor = new DenseTensor<float>(new float[2 * 3 * 4], [1, 1, 2, 12]);
        tensor[0, 0, 1, 5] = float.PositiveInfinity;

        Assert.True(OnnxTiler.ContainsNonFinite(tensor.Buffer.Span));
    }
}
