using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// The decode indexes the output as three NCHW planes, so the tensor's actual layout has to be
/// asserted, not just its element count. An NHWC tensor with the same number of elements passes a
/// count check and is then read as interleaved planes — a silently wrong image. The engine appends
/// every <c>.onnx</c> in the models directory as a last-resort candidate, so a foreign model is a
/// real input here. <see cref="OnnxTiler.EnsureOutputLayout"/> is the guard; these tests pin it
/// directly and pin that a real generated model's shape still decodes through
/// <see cref="OnnxTiler.DecodeOutputTensor"/>.
/// </summary>
public class OnnxTilerOutputLayoutTests : IDisposable
{
    private readonly List<string> _created = [];

    private const int TileW = 2;
    private const int TileH = 2;
    private const int PaddedW = 2;
    private const int PaddedH = 2;
    private const int Scale = 2;

    private const int OutPaddedW = PaddedW * Scale;
    private const int OutPaddedH = PaddedH * Scale;

    [Fact]
    public void EnsureOutputLayout_AcceptsTheNchwShapeTheShippedModelsProduce()
    {
        // Every pinned upscaler (MangaJaNai, IllustrationJaNai) declares
        // [batch, 3, out_height, out_width] and reports concrete dims at runtime.
        OnnxTiler.EnsureOutputLayout(
            [1, 3, OutPaddedH, OutPaddedW],
            OutPaddedW,
            OutPaddedH,
            PaddedW,
            PaddedH,
            Scale
        );
    }

    [Fact]
    public void EnsureOutputLayout_RejectsAnNhwcTensorWithTheSameElementCount()
    {
        // [1, H, W, 3] has exactly 3 * H * W elements — the same count as the NCHW shape — so the
        // old element-count check let it through and the decode read it as three interleaved planes.
        // The dimensions are what tell the two apart.
        var ex = Assert.Throws<ModelContractMismatchException>(() =>
            OnnxTiler.EnsureOutputLayout(
                [1, OutPaddedH, OutPaddedW, 3],
                OutPaddedW,
                OutPaddedH,
                PaddedW,
                PaddedH,
                Scale
            )
        );

        Assert.Contains("1, 4, 4, 3", ex.Message);
        Assert.Contains("NCHW", ex.Message);
    }

    [Fact]
    public void EnsureOutputLayout_RejectsARank3OutputAndAWrongScale()
    {
        // A rank-3 output is not a layout the decoder handles either, and a model at a different real
        // scale produces matching-rank dims of the wrong size. Both must be rejected.
        Assert.Throws<ModelContractMismatchException>(() =>
            OnnxTiler.EnsureOutputLayout(
                [3, OutPaddedH, OutPaddedW],
                OutPaddedW,
                OutPaddedH,
                PaddedW,
                PaddedH,
                Scale
            )
        );
        Assert.Throws<ModelContractMismatchException>(() =>
            OnnxTiler.EnsureOutputLayout(
                [1, 3, OutPaddedH * 2, OutPaddedW * 2],
                OutPaddedW,
                OutPaddedH,
                PaddedW,
                PaddedH,
                Scale
            )
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecodeOutputTensor_AcceptsTheShapeARealModelProduces(bool fp16)
    {
        // The legitimate path: a real generated model at the requested scale, decoded through the
        // real entry point. The layout check must not reject it in either precision branch.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(
            CreateDir(),
            scale: Scale,
            fp16: fp16
        );
        using var session = new InferenceSession(modelPath);

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

    private string CreateDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"output_layout_{Guid.NewGuid():N}");
        _created.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _created)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
