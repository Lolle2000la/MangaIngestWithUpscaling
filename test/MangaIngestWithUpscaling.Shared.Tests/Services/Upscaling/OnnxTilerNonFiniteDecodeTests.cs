using System.Collections;
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
public class OnnxTilerNonFiniteDecodeTests : IDisposable
{
    private readonly List<string> _created = [];

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

    [Fact]
    public void DecodeOutputTensor_RejectsAModelAtADifferentScaleThanRequested()
    {
        // The decode geometry comes from the REQUESTED scale. Candidate selection can fall back to
        // "any model at all" in a models directory that lacks this scale, and such a model indexes
        // with the wrong stride: a lower real scale runs off the end of the tensor, a higher one stays
        // in bounds and decodes finite values — assembling the page from the wrong-scale tensors at
        // the requested size, which is the silent direction. The element count catches both.
        Assert.Throws<InvalidOperationException>(() => DecodeAtRequestedScale(2, requested: Scale));
        Assert.Throws<InvalidOperationException>(() => DecodeAtRequestedScale(Scale, requested: 2));

        // The matched scale still decodes, so the check is not simply rejecting everything.
        Assert.Equal(
            TileW * Scale * TileH * Scale * 3,
            DecodeAtRequestedScale(Scale, requested: Scale).Length
        );
    }

    private static byte[] DecodeAtRequestedScale(int modelScale, int requested)
    {
        // A models directory of its own: the caller's would leave the model under a name that makes
        // the fixture's next use pick the wrong file.
        string dir = Path.Combine(Path.GetTempPath(), $"decode_scale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string modelPath = TinyOnnxModel.WriteNearestUpscaler(dir, scale: modelScale);
            using var session = new InferenceSession(modelPath);

            var input = new DenseTensor<Float16>(
                Enumerable.Repeat(Float16.One, 3 * PaddedW * PaddedH).ToArray(),
                [1, 3, PaddedH, PaddedW]
            );
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([
                NamedOnnxValue.CreateFromTensor(TinyOnnxModel.InputName, input),
            ]);

            return OnnxTiler.DecodeOutputTensor(
                outputs,
                TileW,
                TileH,
                PaddedW,
                PaddedH,
                requested,
                CancellationToken.None
            );
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UpscaleRgb_MultiTileNonFiniteModel_ThrowsFromTheTiledPipeline()
    {
        // The tiled pipeline decodes tile N-1 on a background task and only observes its rejection on
        // the next iteration, by which point tile N's native output already exists and has to be
        // released. That is the recovery path this fix protects, and it is the path a real page takes
        // (the single-tile helper does not pipeline). A model that really returns NaN drives it
        // end to end: 64x64 at a 32px tile size is a 2x2 grid.
        string modelPath = TinyOnnxModel.WriteNonFiniteOutput(CreateDir(), nan: true, fp16: true);
        using var session = new InferenceSession(modelPath);

        Assert.Throws<NonFiniteModelOutputException>(() =>
            OnnxTiler.UpscaleRgb(
                new byte[64 * 64 * 3],
                64,
                64,
                Scale,
                session,
                tileSize: 32,
                cancellationToken: CancellationToken.None
            )
        );
    }

    [Fact]
    public void DecodeOutputTensor_CancelledToken_StillDisposesTheOutputTensor()
    {
        // DecodeOutputTensor owns the outputs it is handed, so it has to release them even when it
        // bails out on cancellation before decoding a single value. The check used to run before the
        // using scope, which leaked the native output tensor on a cancelled tile.
        string modelPath = TinyOnnxModel.WriteNearestUpscaler(CreateDir(), scale: Scale);
        using var session = new InferenceSession(modelPath);

        var input = new DenseTensor<Float16>(
            Enumerable.Repeat(Float16.One, 3 * PaddedW * PaddedH).ToArray(),
            [1, 3, PaddedH, PaddedW]
        );
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> real = session.Run([
            NamedOnnxValue.CreateFromTensor(TinyOnnxModel.InputName, input),
        ]);
        var recording = new RecordingOutputs(real.ToList());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            OnnxTiler.DecodeOutputTensor(
                recording,
                TileW,
                TileH,
                PaddedW,
                PaddedH,
                Scale,
                cts.Token
            )
        );

        Assert.Equal(1, recording.DisposeCount);
    }

    /// <summary>
    /// Wraps a real session's output collection and counts how often it is disposed, so the
    /// ownership contract of <see cref="OnnxTiler.DecodeOutputTensor"/> can be asserted rather than
    /// assumed.
    /// </summary>
    private sealed class RecordingOutputs(IReadOnlyList<DisposableNamedOnnxValue> inner)
        : IDisposableReadOnlyCollection<DisposableNamedOnnxValue>
    {
        public int DisposeCount { get; private set; }

        public int Count => inner.Count;

        public DisposableNamedOnnxValue this[int index] => inner[index];

        public IEnumerator<DisposableNamedOnnxValue> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            DisposeCount++;
            foreach (DisposableNamedOnnxValue value in inner)
            {
                value.Dispose();
            }
        }
    }

    /// <summary>
    /// Runs one generated model through the real tiler decode path. The exception is returned rather
    /// than thrown so the theory can assert on it and on the bytes in one call.
    /// </summary>
    private (byte[] Input, byte[]? Decoded, Exception? Thrown) Run(bool nan, bool fp16)
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

    private string CreateDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nonfinite_decode_{Guid.NewGuid():N}");
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
