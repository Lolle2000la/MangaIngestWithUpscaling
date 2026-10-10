using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class ModelFileNamesTests
{
    // The fp16 and fp32 archives of a model family ship the same file names. The fp32 copies
    // therefore have to be renamed on extraction, or the two precisions overwrite each other and
    // there is nothing to fall back to.

    [Theory]
    [InlineData(
        "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
        "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx"
    )]
    [InlineData(
        "4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx",
        "4x_IllustrationJaNai_V3detail_HAT_L_28k_fp32.onnx"
    )]
    [InlineData(
        "4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx",
        "4x_IllustrationJaNai_V2standard_FDAT_M_52k_fp32.onnx"
    )]
    [InlineData(
        "2x_IllustrationJaNai_V1_ESRGAN_120k.onnx",
        "2x_IllustrationJaNai_V1_ESRGAN_120k_fp32.onnx"
    )]
    public void Resolve_WithFp32Suffix_ReplacesOrAppendsThePrecisionMarker(
        string archiveName,
        string expected
    ) => Assert.Equal(expected, ModelFileNames.Resolve(archiveName, ModelFileNames.Fp32Suffix));

    [Theory]
    [InlineData("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx")]
    [InlineData("4x_IllustrationJaNai_V2standard_FDAT_M_52k.onnx")]
    public void Resolve_WithoutSuffix_KeepsTheShippedName(string archiveName) =>
        Assert.Equal(archiveName, ModelFileNames.Resolve(archiveName, null));

    [Fact]
    public void Resolve_IsInvariantUnderASecondApplication()
    {
        // The fp32 set is downloaded alongside the fp16 one; renaming a name that already carries
        // the suffix must not stack another one.
        string once = ModelFileNames.Resolve(
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
            ModelFileNames.Fp32Suffix
        );
        Assert.Equal(once, ModelFileNames.Resolve(once, ModelFileNames.Fp32Suffix));
    }

    [Fact]
    public void Resolve_KeepsTheExtensionSoScaleDetectionStillWorks()
    {
        string resolved = ModelFileNames.Resolve(
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx",
            ModelFileNames.Fp32Suffix
        );
        Assert.EndsWith(".onnx", resolved, StringComparison.Ordinal);
        Assert.Equal(4, OnnxUpscaleEngine.InferScaleFromModelName(resolved));
        Assert.Equal(ModelArchitecture.FdatM, OnnxTiler.DetectArchitecture(resolved));
    }

    [Fact]
    public void Resolve_ThrowsForAnEmptyName() =>
        Assert.Throws<ArgumentNullException>(() =>
            ModelFileNames.Resolve(null!, ModelFileNames.Fp32Suffix)
        );
}
