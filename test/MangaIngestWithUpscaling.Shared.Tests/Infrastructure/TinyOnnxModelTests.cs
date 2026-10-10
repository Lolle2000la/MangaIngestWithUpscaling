using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Infrastructure;

/// <summary>
/// Pins <see cref="TinyOnnxModel"/> itself, so a mistake in the hand-written ONNX encoding surfaces
/// here rather than as a confusing failure in whichever test asked for a model. Everything it
/// asserts is checked against the real ONNX Runtime, which is the whole point of the generator.
/// </summary>
public class TinyOnnxModelTests : IDisposable
{
    private readonly List<string> _created = [];

    [Fact]
    public void WriteNearestUpscaler_LoadsInOnnxRuntime()
    {
        string path = TinyOnnxModel.WriteNearestUpscaler(CreateDir());

        using var session = new InferenceSession(path);

        Assert.True(session.InputMetadata.ContainsKey(TinyOnnxModel.InputName));
        Assert.True(session.OutputMetadata.ContainsKey(TinyOnnxModel.OutputName));
        Assert.Equal(typeof(Float16), session.InputMetadata[TinyOnnxModel.InputName].ElementType);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void WriteNearestUpscaler_ProducesTheRequestedOutputSize(int scale)
    {
        string path = TinyOnnxModel.WriteNearestUpscaler(CreateDir(), scale);
        using var session = new InferenceSession(path);

        const int width = 13;
        const int height = 7;
        var input = new DenseTensor<Float16>(
            Enumerable.Repeat(Float16.One, 3 * width * height).ToArray(),
            [1, 3, height, width]
        );
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([
            NamedOnnxValue.CreateFromTensor(TinyOnnxModel.InputName, input),
        ]);

        var output = outputs[0].AsTensor<Float16>();
        Assert.Equal(width * scale, output.Dimensions[^1]);
        Assert.Equal(height * scale, output.Dimensions[^2]);
    }

    [Fact]
    public void WriteNearestUpscaler_AcceptsAnySpatialSize()
    {
        // No shape is declared, so the same model file serves every test rather than needing one
        // per size — that is what keeps the generated fixture tiny.
        string path = TinyOnnxModel.WriteNearestUpscaler(CreateDir());
        using var session = new InferenceSession(path);
        int[] sides = [8, 64, 129];

        foreach (int size in sides)
        {
            var input = new DenseTensor<Float16>(
                Enumerable.Repeat(Float16.One, 3 * size * size).ToArray(),
                [1, 3, size, size]
            );
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([
                NamedOnnxValue.CreateFromTensor(TinyOnnxModel.InputName, input),
            ]);
            Assert.Equal(2 * size, outputs[0].AsTensor<Float16>().Dimensions[^1]);
        }
    }

    [Fact]
    public void WriteNearestUpscaler_IsSmallEnoughToBeWorthGenerating()
    {
        string path = TinyOnnxModel.WriteNearestUpscaler(CreateDir());
        Assert.True(
            new FileInfo(path).Length < 4096,
            $"expected a tiny model, got {new FileInfo(path).Length} bytes"
        );
    }

    [Fact]
    public void WriteNearestUpscaler_Fp32_RequestsTheFp32ElementType()
    {
        string path = TinyOnnxModel.WriteNearestUpscaler(CreateDir(), 2, fp16: false);
        using var session = new InferenceSession(path);

        Assert.Equal(typeof(float), session.InputMetadata[TinyOnnxModel.InputName].ElementType);
    }

    [Fact]
    public void WriteIdentity_LoadsInOnnxRuntimeAndPassesValuesThrough()
    {
        string path = TinyOnnxModel.WriteIdentity(CreateDir());
        using var session = new InferenceSession(path);

        var input = new DenseTensor<Float16>(
            new[] { Float16.One, Float16.Zero, Float16.One },
            [1, 1, 1, 3]
        );
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([
            NamedOnnxValue.CreateFromTensor(TinyOnnxModel.InputName, input),
        ]);

        var output = outputs[0].AsTensor<Float16>();
        Assert.Equal(4, output.Dimensions.Length);
        Assert.Equal(1, output.Dimensions[0]);
        Assert.Equal(1, output.Dimensions[1]);
        Assert.Equal(1, output.Dimensions[2]);
        Assert.Equal(3, output.Dimensions[3]);
    }

    private string CreateDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"tiny_onnx_{Guid.NewGuid():N}");
        _created.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _created)
        {
            TryDelete(dir);
        }
    }

    private static void TryDelete(string dir)
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
